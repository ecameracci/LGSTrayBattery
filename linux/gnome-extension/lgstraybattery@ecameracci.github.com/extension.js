import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import St from 'gi://St';
import UPower from 'gi://UPowerGlib';

import {Extension, gettext as _} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

const PANEL_ICON_SIZE = 16;
const MENU_ICON_SIZE = 32;
const OFFLINE_OPACITY = 102;

const GLYPHS = new Map([
    [UPower.DeviceKind.MOUSE, 'Mouse'],
    [UPower.DeviceKind.GAMING_INPUT, 'Mouse'],
    [UPower.DeviceKind.KEYBOARD, 'Keyboard'],
    [UPower.DeviceKind.HEADSET, 'Headset'],
    [UPower.DeviceKind.HEADPHONES, 'Headset'],
]);

function isLogitech(device) {
    return `${device.vendor ?? ''} ${device.model ?? ''}`.toLowerCase().includes('logitech') ||
        (device.native_path ?? '').includes('hidpp_battery');
}

function statusText(state) {
    if (!state.online)
        return _('Disconnected');

    const percent = Math.round(state.percent);
    if (state.charging)
        return _('%d%% (Charging)').format(percent);
    return _('%d%%').format(percent);
}

function indicatorName(state, suffix) {
    if (!state.online)
        return `Missing${suffix}`;
    if (state.charging)
        return `Charging${suffix}`;
    if (state.percent < 10)
        return 'Indicator_10';
    if (state.percent < 50)
        return 'Indicator_30';
    if (state.percent < 85)
        return 'Indicator_50';
    return 'Indicator_100';
}

// Same layered artwork as the Windows tray icon: level indicator, battery outline, device glyph
const DeviceIcon = GObject.registerClass(
class DeviceIcon extends St.Widget {
    _init(iconsDir, size) {
        super._init({
            layout_manager: new Clutter.BinLayout(),
            y_align: Clutter.ActorAlign.CENTER,
        });

        this._iconsDir = iconsDir;
        this._gicons = new Map();
        this._dark = true;
        this._state = null;
        this._numeric = false;

        this._layers = [0, 1, 2].map(() => {
            const layer = new St.Icon({icon_size: size});
            this.add_child(layer);
            return layer;
        });
        this._label = new St.Label({
            y_align: Clutter.ActorAlign.CENTER,
            visible: false,
        });
        this.add_child(this._label);
    }

    update(state, numeric) {
        this._state = state;
        this._numeric = numeric;
        this._sync();
    }

    vfunc_style_changed() {
        super.vfunc_style_changed();

        // Light text means a dark background, which needs the white "_dark" artwork
        const fg = this.get_theme_node().get_foreground_color();
        this._dark = 0.299 * fg.red + 0.587 * fg.green + 0.114 * fg.blue > 127;
        this._sync();
    }

    _gicon(name) {
        let gicon = this._gicons.get(name);
        if (!gicon) {
            gicon = new Gio.FileIcon({file: this._iconsDir.get_child(`${name}.png`)});
            this._gicons.set(name, gicon);
        }
        return gicon;
    }

    _sync() {
        const state = this._state;
        if (!state)
            return;

        const suffix = this._dark ? '_dark' : '';
        const names = [indicatorName(state, suffix), `Battery${suffix}`, `${state.glyph}${suffix}`];

        this.opacity = state.online ? 255 : OFFLINE_OPACITY;
        this._layers.forEach((layer, i) => {
            layer.visible = !this._numeric;
            layer.gicon = this._gicon(names[i]);
        });
        this._label.visible = this._numeric;
        this._label.text = state.online ? `${Math.round(state.percent)}` : '?';
    }
});

const LGSTrayIndicator = GObject.registerClass(
class LGSTrayIndicator extends PanelMenu.Button {
    _init(extension) {
        super._init(0.0, _('LGSTray Battery'));

        this._extension = extension;
        this._settings = extension.getSettings();
        this._iconsDir = extension.dir.get_child('icons');

        // UPower drops wireless devices from its list when their link goes down, so remember
        // every device seen this session to show it as disconnected rather than hide it
        this._known = new Map();
        this._panelIcons = new Map();
        this._watched = new Map();
        this._refreshId = 0;

        this._box = new St.BoxLayout({style_class: 'panel-status-indicators-box'});
        this.add_child(this._box);

        this._settingsChangedId = this._settings.connect('changed', (_settings, key) => {
            if (key === 'logitech-only')
                this._known.clear();
            this._refresh();
        });

        this._client = null;
        this._clientSignals = [];
        try {
            this._client = UPower.Client.new_full(null);
            this._clientSignals = [
                this._client.connect('device-added', () => this._queueRefresh()),
                this._client.connect('device-removed', () => this._queueRefresh()),
            ];
        } catch (e) {
            console.warn(`LGSTray Battery: could not connect to UPower: ${e.message}`);
        }

        this._refresh();
    }

    _queueRefresh() {
        if (this._refreshId)
            return;

        this._refreshId = GLib.idle_add(GLib.PRIORITY_DEFAULT_IDLE, () => {
            this._refreshId = 0;
            this._refresh();
            return GLib.SOURCE_REMOVE;
        });
    }

    _syncWatched(devices) {
        const paths = new Set();
        for (const device of devices) {
            const path = device.get_object_path();
            paths.add(path);
            if (!this._watched.has(path))
                this._watched.set(path, {device, id: device.connect('notify', () => this._queueRefresh())});
        }

        for (const [path, {device, id}] of this._watched) {
            if (!paths.has(path)) {
                device.disconnect(id);
                this._watched.delete(path);
            }
        }
    }

    _refresh() {
        const devices = this._client?.get_devices() ?? [];
        this._syncWatched(devices);

        const logitechOnly = this._settings.get_boolean('logitech-only');
        const seen = new Set();

        for (const device of devices) {
            const glyph = GLYPHS.get(device.kind);
            if (!glyph || device.power_supply || (logitechOnly && !isLogitech(device)))
                continue;

            const name = device.model || device.vendor || _('Unknown device');
            const key = device.serial || `${device.vendor}/${name}`;
            seen.add(key);
            this._known.set(key, {
                name,
                glyph,
                percent: device.percentage,
                online: device.is_present,
                // FULLY_CHARGED isn't used: UPower reports it for any device at 100%, plugged in or not
                charging: device.state === UPower.DeviceState.CHARGING ||
                    device.state === UPower.DeviceState.PENDING_CHARGE,
            });
        }

        for (const [key, state] of this._known) {
            if (!seen.has(key))
                state.online = false;
        }

        this._syncPanel();
        this._syncMenu();
    }

    _syncPanel() {
        const numeric = this._settings.get_boolean('show-percentage');

        for (const [key, icon] of this._panelIcons) {
            if (!this._known.has(key)) {
                icon.destroy();
                this._panelIcons.delete(key);
            }
        }

        for (const key of [...this._known.keys()].sort()) {
            let icon = this._panelIcons.get(key);
            if (!icon) {
                icon = new DeviceIcon(this._iconsDir, PANEL_ICON_SIZE);
                this._panelIcons.set(key, icon);
                this._box.add_child(icon);
            }
            icon.update(this._known.get(key), numeric);
        }

        this.visible = this._known.size > 0;
    }

    _syncMenu() {
        this.menu.removeAll();

        for (const key of [...this._known.keys()].sort()) {
            const state = this._known.get(key);
            const item = new PopupMenu.PopupBaseMenuItem();

            const icon = new DeviceIcon(this._iconsDir, MENU_ICON_SIZE);
            icon.update(state, false);
            item.add_child(icon);

            const labels = new St.Widget({
                layout_manager: new Clutter.BoxLayout({orientation: Clutter.Orientation.VERTICAL}),
                y_align: Clutter.ActorAlign.CENTER,
            });
            labels.add_child(new St.Label({text: state.name}));
            labels.add_child(new St.Label({text: statusText(state), opacity: 180}));
            item.add_child(labels);

            this.menu.addMenuItem(item);
        }

        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this.menu.addAction(_('Settings'), () => this._extension.openPreferences());
    }

    destroy() {
        if (this._refreshId) {
            GLib.source_remove(this._refreshId);
            this._refreshId = 0;
        }
        for (const {device, id} of this._watched.values())
            device.disconnect(id);
        this._watched.clear();
        for (const id of this._clientSignals)
            this._client.disconnect(id);
        this._clientSignals = [];
        this._client = null;
        this._settings.disconnect(this._settingsChangedId);
        this._settings = null;

        super.destroy();
    }
});

export default class LGSTrayBatteryExtension extends Extension {
    enable() {
        this._indicator = new LGSTrayIndicator(this);
        Main.panel.addToStatusArea(this.uuid, this._indicator);
    }

    disable() {
        this._indicator?.destroy();
        this._indicator = null;
    }
}

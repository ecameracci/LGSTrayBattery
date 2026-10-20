import Adw from 'gi://Adw';
import Gio from 'gi://Gio';

import {ExtensionPreferences, gettext as _} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';

export default class LGSTrayBatteryPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        const settings = this.getSettings();
        const page = new Adw.PreferencesPage();
        const group = new Adw.PreferencesGroup();
        page.add(group);

        const rows = [
            ['show-percentage', _('Show battery percentage as a number'), _('Replaces the device icon in the top bar')],
            ['logitech-only', _('Only show Logitech devices'), _('Turn off to include any wireless mouse, keyboard or headset')],
        ];
        for (const [key, title, subtitle] of rows) {
            const row = new Adw.SwitchRow({title, subtitle});
            settings.bind(key, row, 'active', Gio.SettingsBindFlags.DEFAULT);
            group.add(row);
        }

        window.add(page);
    }
}

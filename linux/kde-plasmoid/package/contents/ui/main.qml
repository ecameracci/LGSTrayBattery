import QtQuick
import QtQuick.Layouts
import org.kde.plasma.plasmoid
import org.kde.plasma.core as PlasmaCore
import org.kde.plasma.plasma5support as P5Support
import org.kde.plasma.components as PlasmaComponents
import org.kde.plasma.extras as PlasmaExtras
import org.kde.kirigami as Kirigami

PlasmoidItem {
    id: root

    property var devices: []
    // UPower drops wireless devices from its list when their link goes down, so remember
    // every device seen this session to show it as disconnected rather than hide it
    property var known: ({})

    readonly property var peripheralTypes: ["Mouse", "Keyboard", "Headset", "Headphone", "GamingInput"]

    function statusText(device) {
        if (!device.online) {
            return i18n("Disconnected");
        }
        const percent = Math.round(device.percent);
        if (device.charging) {
            return i18n("%1% (Charging)", percent);
        }
        return i18n("%1%", percent);
    }

    function refresh() {
        const batteries = pmSource.data["Battery"];
        const sources = batteries ? (batteries["Sources"] || []) : [];
        const seen = {};

        for (const source of sources) {
            const data = pmSource.data[source];
            if (!data || data["Is Power Supply"] || !peripheralTypes.includes(data["Type"])) {
                continue;
            }

            const vendor = data["Vendor"] || "";
            const name = data["Product"] || data["Pretty Name"] || source;
            if (Plasmoid.configuration.logitechOnly && !(vendor + " " + name).toLowerCase().includes("logitech")) {
                continue;
            }

            const key = vendor + "/" + name;
            seen[key] = true;
            known[key] = {
                name: name,
                type: data["Type"],
                percent: data["Percent"] || 0,
                online: data["Plugged in"] !== false,
                // "FullyCharged" isn't used: UPower reports it for any device at 100%, plugged in or not
                charging: data["State"] === "Charging",
            };
        }

        for (const key in known) {
            if (!seen[key]) {
                known[key].online = false;
            }
        }

        devices = Object.keys(known).sort().map(key => Object.assign({}, known[key]));
    }

    Plasmoid.icon: "input-mouse"
    Plasmoid.status: devices.length > 0 ? PlasmaCore.Types.ActiveStatus : PlasmaCore.Types.PassiveStatus

    toolTipMainText: i18n("LGSTray Battery")
    toolTipSubText: devices.length > 0
        ? devices.map(device => device.name + ", " + statusText(device)).join("\n")
        : i18n("No devices found")

    switchWidth: Kirigami.Units.gridUnit * 10
    switchHeight: Kirigami.Units.gridUnit * 6

    P5Support.DataSource {
        id: pmSource

        engine: "powermanagement"
        connectedSources: sources

        onSourceAdded: source => {
            disconnectSource(source);
            connectSource(source);
        }
        onSourceRemoved: source => {
            disconnectSource(source);
            Qt.callLater(root.refresh);
        }
        onNewData: Qt.callLater(root.refresh)
    }

    Connections {
        target: Plasmoid.configuration

        function onLogitechOnlyChanged() {
            root.known = {};
            root.refresh();
        }
    }

    compactRepresentation: MouseArea {
        id: compact

        readonly property bool vertical: Plasmoid.formFactor === PlasmaCore.Types.Vertical
        readonly property int count: Math.max(1, root.devices.length)
        readonly property real iconSize: vertical ? Math.min(width, height / count) : Math.min(height, width / count)

        Layout.minimumWidth: vertical ? 0 : height * count
        Layout.minimumHeight: vertical ? width * count : 0

        hoverEnabled: true
        onClicked: root.expanded = !root.expanded

        Grid {
            anchors.centerIn: parent
            columns: compact.vertical ? 1 : compact.count

            Repeater {
                model: root.devices

                DeviceIcon {
                    required property var modelData

                    device: modelData
                    numeric: Plasmoid.configuration.showPercentage
                    width: compact.iconSize
                    height: compact.iconSize
                }
            }

            Kirigami.Icon {
                visible: root.devices.length === 0
                width: compact.iconSize
                height: compact.iconSize
                source: "input-mouse"
                opacity: 0.4
            }
        }
    }

    fullRepresentation: ColumnLayout {
        Layout.minimumWidth: Kirigami.Units.gridUnit * 16
        Layout.minimumHeight: Kirigami.Units.gridUnit * 6
        spacing: Kirigami.Units.largeSpacing

        PlasmaExtras.PlaceholderMessage {
            visible: root.devices.length === 0
            Layout.fillWidth: true
            Layout.alignment: Qt.AlignCenter
            iconName: "input-mouse"
            text: i18n("No devices found")
            explanation: Plasmoid.configuration.logitechOnly
                ? i18n("Turn on a Logitech mouse, keyboard or headset, or connect its receiver.")
                : i18n("Turn on a wireless mouse, keyboard or headset.")
        }

        Repeater {
            model: root.devices

            RowLayout {
                required property var modelData

                Layout.fillWidth: true
                spacing: Kirigami.Units.largeSpacing

                DeviceIcon {
                    device: modelData
                    Layout.preferredWidth: Kirigami.Units.iconSizes.large
                    Layout.preferredHeight: Kirigami.Units.iconSizes.large
                }

                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 0

                    PlasmaExtras.Heading {
                        Layout.fillWidth: true
                        level: 4
                        text: modelData.name
                        elide: Text.ElideRight
                    }

                    PlasmaComponents.Label {
                        Layout.fillWidth: true
                        text: root.statusText(modelData)
                        opacity: 0.7
                    }
                }
            }
        }

        Item {
            Layout.fillHeight: true
        }
    }
}

import QtQuick
import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents

// Same layered artwork as the Windows tray icon: level indicator, battery outline, device glyph
Item {
    id: icon

    required property var device
    property bool numeric: false

    // Light text means a dark background, which needs the white "_dark" artwork
    readonly property string suffix: Kirigami.Theme.textColor.hsvValue > 0.5 ? "_dark" : ""

    readonly property string glyph: {
        switch (device.type) {
        case "Keyboard":
            return "Keyboard";
        case "Headset":
        case "Headphone":
            return "Headset";
        default:
            return "Mouse";
        }
    }

    readonly property string indicator: {
        if (!device.online) {
            return "Missing" + suffix;
        }
        if (device.charging) {
            return "Charging" + suffix;
        }
        if (device.percent < 10) {
            return "Indicator_10";
        }
        if (device.percent < 50) {
            return "Indicator_30";
        }
        if (device.percent < 85) {
            return "Indicator_50";
        }
        return "Indicator_100";
    }

    opacity: device.online ? 1 : 0.4

    Repeater {
        model: icon.numeric ? [] : [icon.indicator, "Battery" + icon.suffix, icon.glyph + icon.suffix]

        Image {
            required property string modelData

            anchors.fill: parent
            source: Qt.resolvedUrl("../images/" + modelData + ".png")
            fillMode: Image.PreserveAspectFit
            smooth: true
            mipmap: true
        }
    }

    PlasmaComponents.Label {
        visible: icon.numeric
        anchors.fill: parent
        text: icon.device.online ? Math.round(icon.device.percent) : "?"
        horizontalAlignment: Text.AlignHCenter
        verticalAlignment: Text.AlignVCenter
        fontSizeMode: Text.Fit
        minimumPixelSize: 6
        font.pixelSize: height
    }
}

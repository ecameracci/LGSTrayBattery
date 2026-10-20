import QtQuick
import QtQuick.Controls as QQC2
import org.kde.kirigami as Kirigami
import org.kde.kcmutils as KCM

KCM.SimpleKCM {
    property alias cfg_showPercentage: showPercentage.checked
    property alias cfg_logitechOnly: logitechOnly.checked

    Kirigami.FormLayout {
        QQC2.CheckBox {
            id: showPercentage
            Kirigami.FormData.label: i18n("Panel:")
            text: i18n("Show battery percentage as a number")
        }

        QQC2.CheckBox {
            id: logitechOnly
            Kirigami.FormData.label: i18n("Devices:")
            text: i18n("Only show Logitech devices")
        }
    }
}

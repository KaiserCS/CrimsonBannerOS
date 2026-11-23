/* SPDX-License-Identifier: GPL-3.0-or-later
 * The slides Calamares shows while it installs Crimson Banner OS. */
import QtQuick 2.0;
import calamares.slideshow 1.0;

Presentation {
    id: presentation

    Timer {
        interval: 9000
        running: presentation.activatedInCalamares
        repeat: true
        onTriggered: presentation.goToNextSlide()
    }

    component Card: Slide {
        property string heading
        property string body
        Rectangle { anchors.fill: parent; color: "#141416" }
        Image { id: logo; source: "logo.png"; width: 88; height: 88; anchors.horizontalCenter: parent.horizontalCenter; y: parent.height * 0.14 }
        Text {
            id: title
            anchors.top: logo.bottom; anchors.topMargin: 22
            width: parent.width * 0.8; anchors.horizontalCenter: parent.horizontalCenter
            horizontalAlignment: Text.AlignHCenter; wrapMode: Text.WordWrap
            color: "#E8E9EC"; font.pixelSize: 26; font.bold: true
            text: heading
        }
        Text {
            anchors.top: title.bottom; anchors.topMargin: 14
            width: parent.width * 0.7; anchors.horizontalCenter: parent.horizontalCenter
            horizontalAlignment: Text.AlignHCenter; wrapMode: Text.WordWrap
            color: "#A8ACB2"; font.pixelSize: 16; lineHeight: 1.3
            text: body
        }
    }

    Card {
        heading: qsTr("Installing Crimson Banner OS")
        body: qsTr("Everything comes from this medium: no network is needed, and nothing is sent anywhere.")
    }
    Card {
        heading: qsTr("Free software, from top to bottom")
        body: qsTr("The desktop, its compositor and window manager, the browser and the apps are Crimson Banner OS's own, under the GPL.")
    }
    Card {
        heading: qsTr("More programs: Software")
        body: qsTr("Our own apps, Flatpaks from Flathub and AppImages, in one place. Only free software is shown.")
    }
    Card {
        heading: qsTr("Updates")
        body: qsTr("Settings → Software Updates keeps the system current with pacman, behind your password.")
    }

    function onActivate() { presentation.currentSlide = 0; }
    function onLeave() { }
}

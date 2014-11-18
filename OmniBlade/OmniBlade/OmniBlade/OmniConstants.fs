﻿// OmniBlade.
// Copyright (C) Bryan Edds, 2013-2014.

namespace OmniBlade
open Prime
open Nu
open Nu.Constants
module OmniConstants =

    // misc constants
    let GuiPackageName = "Gui"
    let FieldFeelerName = "Feeler"
    let FieldAvatarName = "Avatar"

    // asset constants
    let GameSong = { SongPackageName = DefaultPackageName; SongAssetName = "Song" }

    // transition constants
    let IncomingTime = 20L
    let OutgoingTime = 30L
    let StageOutgoingTime = 90L

    // splash constants
    let SplashAddress = stoa<Screen> "Splash"
    let SplashIncomingTime = 60L
    let SplashIdlingTime = 60L
    let SplashOutgoingTime = 40L

    // title constants
    let TitleAddress = stoa<Screen> "Title"
    let TitleGroupFilePath = "Assets/OmniBlade/Groups/Title.nugroup"
    let ClickTitleNewGameEvent = stoa<unit> "Click/Title/Group/NewGame"
    let ClickTitleLoadGameEvent = stoa<unit> "Click/Title/Group/LoadGame"
    let ClickTitleCreditsEvent = stoa<unit> "Click/Title/Group/Credits"
    let ClickTitleExitEvent = stoa<unit> "Click/Title/Group/Exit"

    // load game constants
    let LoadGameAddress = stoa<Screen> "LoadGame"
    let LoadGameGroupFilePath = "Assets/OmniBlade/Groups/LoadGame.nugroup"
    let ClickLoadGameBackEvent = stoa<unit> "Click/LoadGame/Group/Back"

    // credits constants
    let CreditsAddress = stoa<Screen> "Credits"
    let CreditsGroupFilePath = "Assets/OmniBlade/Groups/Credits.nugroup"
    let ClickCreditsBackEvent = stoa<unit> "Click/Credits/Group/Back"

    // field constants
    let FieldAddress = stoa<Screen> "Field"
    let FieldGroupFilePath = "Assets/OmniBlade/Groups/Field.nugroup"
    let ClickFieldBackEvent = stoa<unit> "Click/Field/Group/Back"

    // time constants
    let TimeAddress = stoa<Screen> "Time"

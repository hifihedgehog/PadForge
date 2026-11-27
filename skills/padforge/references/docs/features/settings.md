<!-- https://padforge.org/docs/features/settings/ as PadForge's skill carries it, from padforge.org's wiki/features/settings.md. -->
# Settings

*Language, theme, engine timing, window behavior, hardware support, drivers and the settings file, on one page at the foot of the sidebar.*

![Settings page with its group buttons and the General cards](https://padforge.org/docs/images/settings.png)

---

## Page layout

![Search settings with the word notify in it: the matching rows of the Input group, with Battery Alerts’ More rows open](https://padforge.org/docs/images/settings-search.png)

**Settings** sits in the sidebar's footer, beside About. Its head holds **Search settings**, which filters the cards to the settings whose names match ("No settings match “…”." says when none does). Five group buttons stand beside the cards, or over them on a narrow window, and each jumps to its group:

| Group | Cards |
|-------|-------|
| **General** | [Language](#language), [Appearance](#appearance), [Window](#window), [Updates](#updates) |
| **Input** | [Input Engine](#input-engine), [Assignment Prompts](#assignment-prompts), [Battery Alerts](#battery-alerts) |
| **Hardware** | [Hardware Support](#hardware-support) |
| **Drivers** | [Drivers](#drivers), with the HidHide Options fold |
| **Data & Privacy** | [Community Configs](#community-configs), [Settings File](#settings-file), [Diagnostics](#diagnostics) |

Most cards carry a **Reset All** for their own settings, and most settings a reset button of their own.

---

## Language

A dropdown at the top of the page lists each language under its own name, with its English name beside it when the two differ. Typing jumps to a language by its own name. Changes apply right away. No restart. The picked language saves and comes back on the next launch.

![The Language dropdown open, each language under its own name with its English name beside it](https://padforge.org/docs/images/settings-language-list.png)

With no language picked, PadForge follows the Windows display language: its own translation, else the one its speakers read. Austrian German reads German, Taiwanese Chinese reads Traditional Chinese, and Norwegian Bokmål and Nynorsk read Norwegian. English reads British English in the regions whose English follows British conventions, such as the United Kingdom, Ireland, Australia, New Zealand, India and South Africa, and American English everywhere else. A language with no translation shows American English.

The interface comes in 112 languages, 45 of them complete. A partial translation shows English wherever it has no string: its translator left out what could not be worded correctly rather than make it up. British English holds only the strings it writes differently and reads the American text for the rest. Galactic Basic is the American text drawn in the Aurebesh alphabet. No native speaker has reviewed the translations.

Arabic, Central Kurdish, Persian and Urdu lay the interface out right to left. Controller drawings, curves and other pictures keep their left-to-right layout, so a left stick stays on the left. The touchpad overlay, the on-screen menus and the light gun's calibration window stay left to right whole.

![The Settings page in Arabic, laid out right to left](https://padforge.org/docs/images/settings-language-rtl.png)

| Language | Own name | Tag | Strings |
|---|---|---|---|
| Afrikaans | Afrikaans | `af` | 5712 of 6620 |
| Albanian | Shqip | `sq` | 5829 of 6620 |
| Amharic | አማርኛ | `am` | 5855 of 6620 |
| Arabic | العربية | `ar` | 5893 of 6620, right to left |
| Armenian | Հայերեն | `hy` | 5845 of 6620 |
| Basque | Euskara | `eu` | 5877 of 6620 |
| Belarusian | Беларуская | `be` | all |
| Bengali | বাংলা | `bn` | all |
| Bikol | Bikol | `bik` | 1626 of 6620 |
| Bislama | Bislama | `bi` | 2199 of 6620 |
| Bulgarian | Български | `bg` | all |
| Burmese | မြန်မာ | `my` | 5874 of 6620 |
| Cantonese (Traditional) | 廣東話（繁體） | `zh-HK` | all |
| Cape Verdean Creole | Kabuverdianu | `kea` | 3639 of 6620 |
| Catalan | Català | `ca` | 5892 of 6620 |
| Cebuano | Cebuano | `ceb` | 4702 of 6620 |
| Central Kurdish | کوردیی ناوەندی | `ckb` | 5877 of 6620, right to left |
| Chichewa | Chichewa | `ny` | 1887 of 6620 |
| Chinese (Pinyin) | Hànyǔ Pīnyīn | `zh-Latn` | 5802 of 6620 |
| Chinese (Simplified) | 中文（简体） | `zh-Hans` | all |
| Chinese (Traditional) | 中文（繁體） | `zh-Hant` | all |
| Chuukese | Fosun Chuuk | `chk` | 1004 of 6620 |
| Croatian | Hrvatski | `hr` | 5884 of 6620 |
| Czech | Čeština | `cs` | all |
| Danish | Dansk | `da` | all |
| Dutch | Nederlands | `nl` | all |
| English (United Kingdom) | English (United Kingdom) | `en-GB` | all |
| English (United States) | English (United States) | `en-US` | all |
| Estonian | Eesti | `et` | 5883 of 6620 |
| Fiji Hindi | Fiji Hindi | `hif` | 1520 of 6620 |
| Fijian | Vosa Vakaviti | `fj` | 826 of 6620 |
| Finnish | Suomi | `fi` | all |
| French | Français | `fr` | all |
| Galactic Basic (Aurebesh) | Galactic Basic | `en-Qaaa` | all |
| Georgian | ქართული | `ka` | 5866 of 6620 |
| German | Deutsch | `de` | all |
| Greek | Ελληνικά | `el` | all |
| Guarani | Avañeʼẽ | `gn` | 1626 of 6620 |
| Gujarati | ગુજરાતી | `gu` | 5875 of 6620 |
| Haitian Creole | Kreyòl ayisyen | `ht` | 5212 of 6620 |
| Hawaiian | ʻŌlelo Hawaiʻi | `haw` | 1521 of 6620 |
| Hiligaynon | Hiligaynon | `hil` | 2147 of 6620 |
| Hindi | हिन्दी | `hi` | all |
| Hmong | Hmoob | `hmn` | 1999 of 6620 |
| Hungarian | Magyar | `hu` | all |
| Icelandic | Íslenska | `is` | all |
| Igbo | Igbo | `ig` | 1859 of 6620 |
| Ilocano | Ilokano | `ilo` | 1730 of 6620 |
| Indonesian | Bahasa Indonesia | `id` | all |
| Italian | Italiano | `it` | all |
| Japanese | 日本語 | `ja` | all |
| Kannada | ಕನ್ನಡ | `kn` | 5848 of 6620 |
| Kazakh | Қазақша | `kk` | 5880 of 6620 |
| Khmer | ភាសាខ្មែរ | `km` | 5880 of 6620 |
| Kinyarwanda | Ikinyarwanda | `rw` | 2279 of 6620 |
| Klingon | tlhIngan Hol | `tlh` | all |
| Korean | 한국어 | `ko` | all |
| Lao | ລາວ | `lo` | 5865 of 6620 |
| Latvian | Latviešu | `lv` | all |
| Lingala | Lingála | `ln` | 2399 of 6620 |
| Lithuanian | Lietuvių | `lt` | 5880 of 6620 |
| Luganda | Luganda | `lg` | 1124 of 6620 |
| Luxembourgish | Lëtzebuergesch | `lb` | 5841 of 6620 |
| Macedonian | Македонски | `mk` | all |
| Malagasy | Malagasy | `mg` | 2676 of 6620 |
| Malay | Bahasa Melayu | `ms` | 5881 of 6620 |
| Malayalam | മലയാളം | `ml` | 5850 of 6620 |
| Maltese | Malti | `mt` | 5814 of 6620 |
| Marathi | मराठी | `mr` | 5865 of 6620 |
| Mongolian | Монгол | `mn` | 5866 of 6620 |
| Māori | Te Reo Māori | `mi` | 2531 of 6620 |
| Nepali | नेपाली | `ne` | all |
| Northern Sotho | Sepedi | `nso` | 1682 of 6620 |
| Norwegian | Norsk | `no` | all |
| Oromo | Afaan Oromoo | `om` | 2514 of 6620 |
| Papiamento | Papiamentu | `pap` | 3339 of 6620 |
| Persian | فارسی | `fa` | all, right to left |
| Polish | Polski | `pl` | all |
| Portuguese (Brazil) | Português (Brasil) | `pt-BR` | all |
| Portuguese (Portugal) | Português (Portugal) | `pt-PT` | 5885 of 6620 |
| Quechua (Bolivia) | Qhichwa (Bolivia) | `quh` | 1986 of 6620 |
| Romanian | Română | `ro` | all |
| Russian | Русский | `ru` | all |
| Samoan | Gagana Sāmoa | `sm` | 1540 of 6620 |
| Serbian | Српски | `sr` | all |
| Sesotho | Sesotho | `st` | 1544 of 6620 |
| Shona | ChiShona | `sn` | 1865 of 6620 |
| Sindarin (Elvish) | Eglathrin | `sjn` | all |
| Sinhala | සිංහල | `si` | 5841 of 6620 |
| Slovak | Slovenčina | `sk` | all |
| Slovenian | Slovenščina | `sl` | all |
| Spanish | Español | `es` | all |
| Swahili | Kiswahili | `sw` | 5815 of 6620 |
| Swedish | Svenska | `sv` | all |
| Tagalog | Tagalog | `fil` | 5691 of 6620 |
| Tamil | தமிழ் | `ta` | all |
| Telugu | తెలుగు | `te` | all |
| Thai | ไทย | `th` | all |
| Tok Pisin | Tok Pisin | `tpi` | 2539 of 6620 |
| Tongan | Lea faka-Tonga | `to` | 850 of 6620 |
| Tswana | Setswana | `tn` | 1518 of 6620 |
| Turkish | Türkçe | `tr` | all |
| Twi | Twi | `tw` | 994 of 6620 |
| Ukrainian | Українська | `uk` | 5881 of 6620 |
| Urdu | اردو | `ur` | all, right to left |
| Vietnamese | Tiếng Việt | `vi` | all |
| Waray | Winaray | `war` | 1907 of 6620 |
| Welsh | Cymraeg | `cy` | 5852 of 6620 |
| Western Armenian | Արեւմտահայերէն | `hyw` | 5837 of 6620 |
| Xhosa | IsiXhosa | `xh` | 2259 of 6620 |
| Yoruba | Èdè Yorùbá | `yo` | 2487 of 6620 |
| Zulu | IsiZulu | `zu` | 3396 of 6620 |

Corrections and new translations land as pull requests. `tools/l10n/check_translation.py` checks a translation against the rules every file in the tree passes: placeholders, line breaks, digits, the names that stay in Latin letters, and decimal numbers written with a period.

> Profile Shortcuts dropdowns keep their picked value when you switch language. See [Profiles](../guides/profiles.md).

---

## Appearance

### Theme

The **Theme** row picks PadForge's look from a gallery: **System Default**, **Light** and **Dark**, then the ten starter themes, then the theme files you add or build. **New Theme…**, **Add Theme…** and **Save a Copy…** stand in the row. See [Themes](https://padforge.org/docs/features/themes/).

| Option | Behavior |
|---|---|
| **System Default** | Follows your Windows theme, read at launch and again whenever you re-pick this option. |
| **Light** | Always light. |
| **Dark** | Always dark. |
| A starter or a theme file | Its own colors on its own base, dark or light, with its picture behind the top of the page. |

Changes apply right away. The row's reset button returns to **System Default**.

The 2D-versus-3D controller view is set on each controller's page, not here. See [3D and 2D Visualization](https://padforge.org/docs/features/visualization/).

---

## Window

Controls how PadForge acts as a Windows app. Five switches here. Combine them with **Auto-Start Engine on Launch** (in the Input Engine card) for a fully background install.

### Minimize to System Tray

Sends PadForge to the notification area instead of the taskbar when you minimize. Double-click the tray icon to bring the window back.

### Close to System Tray

Keeps PadForge running when you click the window's close button or press Alt+F4. The window hides in the notification area and the engine keeps going. Double-click the tray icon or pick **Show PadForge** to bring the window back, and pick **Exit** in its menu to quit.

### Always Show in System Tray

Keeps the tray icon visible while the window is open, so **Show PadForge** and **Exit** in its menu are always one click away. Off by default. Pair it with **Close to System Tray** to quit from the tray without closing the window first.

### Start Minimized

Opens PadForge minimized to the taskbar. Combine with **Minimize to System Tray** for a launch with only a tray icon.

### Start at Login

Registers a logon scheduled task so PadForge runs when you log into Windows. PadForge is elevated, and Windows will not launch an elevated app from a Startup-folder shortcut, so the task runs it at highest privileges with no UAC prompt.

> **Hands-off setup:** turn on **Auto-Start Engine on Launch**, **Start Minimized**, **Minimize to System Tray** and **Start at Login**. PadForge launches at login, starts the engine and sits in the tray. Virtual controllers are live by the time you open a game.

---

## Updates

![The Updates card in Settings, after a check](https://padforge.org/docs/images/settings-updates.png)

Checks GitHub for new versions of PadForge and installs them from here.

- **Check for Updates Automatically** is on by default. It checks 20 seconds after launch and every 12 hours after that.
- **Install Updates Automatically** downloads a new version in the background and installs it the next time PadForge starts. It shows only while the automatic check is on.
- **Include Pre-Releases** also offers the dev build of the latest commit.
- **Check Now** checks right away. **Install and Restart** and **Release Notes** appear once a newer version is found.

PadForge contacts only GitHub, and the only data it sends is its own version number. [Updates](https://padforge.org/docs/features/updates/) covers what an install does, pre-releases, and every message the card can show.

The card's **Controller Database** row downloads newer rows of the community controller database when you click **Update Now**, and its **More** holds **Your Rows**. See [Controller Database](controller-database.md).

---

## Input Engine

![The Input Engine card in the Input group of Settings](https://padforge.org/docs/images/settings-input-engine.png)

Controls the polling loop that reads physical controllers, runs mappings and deadzones, and writes the virtual controllers.

### Auto-Start Engine on Launch

Starts the engine when PadForge opens. On by default. Off means you start it by hand with the engine's flame on [Home](dashboard.md).

Turn it off if you want PadForge to open without making virtual devices live.

### Continue Polling When Window Loses Focus

Keeps the engine running when PadForge is not the foreground window.

**Leave this on.** Without it, the engine stops the moment you tab into a game and virtual controllers go dead. Turn it off only if you use PadForge as a passive diagnostic.

### Polling Rate

The row is labeled **Polling Rate (Unless a Profile Overrides It)**: a number box in Hz, from 60 to 10000. Default: **1000 Hz**. It is how many times a second the engine reads every device and updates the virtual controllers.

A higher rate uses more processor time. Most users should leave the default. Lower it when the processor is busy or the machine runs on battery.

The box's tooltip gives this PC's wake rate: "On this PC, Windows wakes the engine at most *N* times a second, so a higher rate runs several passes at each wake." *N* is read from Windows' finest timer period, so it is 2000 on a PC whose timer reaches half a millisecond and 1000 on one that reaches one millisecond. A rate above *N* runs its passes in groups at each wake.

While the active profile sets its own polling rate, that rate applies instead, and a note beside the box reads "Overridden while profile “*name*” is active: *rate* Hz". A profile's dialog offers 10000, 8000, 4000, 2000, 1000, 500, 250, 125 and 60 Hz. See [Profiles](../guides/profiles.md).

A settings file from PadForge 5 stored a polling interval of 1 to 16 ms. PadForge 6 reads it as the matching rate, to the nearest hertz: 1 ms becomes 1000 Hz and 16 ms becomes 63 Hz.

### Inactivity Timeout

Seconds a virtual controller stays alive after all of its mapped physical devices go offline. Default: **60**. The unit beside the box reads "seconds (0 = never)": set **0** and the controller stays around indefinitely. One timeout governs every virtual controller type: Xbox, PlayStation, Nintendo, Extended, VR, Keyboard + Mouse and MIDI.

When a controller's mapped pads disconnect (laptop sleeps, USB hub unplugged, battery dies), PadForge holds the virtual controller open for this many seconds. If the devices come back, the controller stays in place. If not, PadForge tears it down to free its kernel slot (the XInput slot, for Xbox types). Surviving controllers of the same type then re-bind to lower kernel slots so the order stays contiguous, and shift back as controllers reactivate. The controller's configuration is kept either way. Plug the devices back in and the virtual controller comes back on its own.

Range: 0 to 3600 seconds.

---

## Assignment Prompts

PadForge can offer a device a place when it connects. Nothing is assigned until you accept an offer. Keyboards, keyboard screens, mice, touchpads and media keys are never offered. All three switches are on by default.

<!-- SCREENSHOT: settings-assignment-prompts -->
![Settings page, Assignment Prompts card with its three switches](https://padforge.org/docs/images/settings-assignment-prompts.png)

- **Offer New Devices to the Open Virtual Controller** fires when a device PadForge has never seen connects while a controller's page is open. Devices it already knows connect without a prompt.
- **Offer Any Connecting Device When the Open Virtual Controller Has No Devices** fires for any device, new or already known, as long as the open controller has nothing assigned yet. Once it has a device, this prompt no longer appears.
- **Offer New Devices on Home** shows [Home's offer](dashboard.md#the-offer) once for each new game controller that no virtual controller uses. **Use It** opens Add a Controller for it, and **Not Now** ends the offer for that device. Turning the switch off takes the offer down at once.

On a controller's page the offer is a band under the **Driven by** line: "*Name* just connected. Assign it to this virtual controller?" with **Assign** and **Not Now**. Assign runs the same path as dragging the device onto the controller: the auto-map, the input hiding defaults and the Mappings refresh all happen as they do for a dropped device. Not Now dismisses that device for that controller until PadForge restarts. Neither page prompt fires on startup or when the engine restarts, since both re-enumerate every device at once.

PadForge never assigns a device on its own. Sim rigs and arcade setups carry many devices with no obvious home for each, so the app only ever offers, and every offer can be switched off here.

---

## Battery Alerts

Get notified when a controller’s battery runs low, before it dies mid-game.

- **Notify When Battery Runs Low** turns the low-battery notice on. It is on by default.
- **Notify at or Below** sets the percentage that triggers it, 5 to 50. Default: 15. The Devices page draws a battery at or under it in gold, with the word **Low**.
- **Check the Notice** holds **Test Notification**, which sends a sample alert at the threshold level through the same path, so you can check it without draining a battery.
- Under **More**:
    - **Also Vibrate the Controller** adds a buzz on the pad itself, so the warning reaches you without looking at the screen. Off by default.
    - **Repeat the Notice Every** shows the low notice again after this many minutes while the pad stays at or below the threshold and is not charging. Range 0 to 60, with the unit "minutes (0 = never)". Default: 0, which never repeats.
    - **Notify When Fully Charged** shows a **Controller Fully Charged** notice once when a pad's charge rises to 100%. It is off by default and works without the low-battery notice. A pad already at 100% when PadForge first sees it stays quiet, and the notice arms again once the level falls to 90% or below.

The low notice fires when the level crosses to the threshold or below, or when PadForge first sees a pad already there. It never fires while the pad charges, and it arms again when the pad charges or rises more than 5 points above the threshold. It shows as a tray notification and in the message line.

The same rumble drives **Test Rumble** on the Devices page: buzzing a device is how you tell which of several identical pads is which.

![The Battery Alerts card](https://padforge.org/docs/images/settings-battery-alerts.png)

---

## Hardware Support

One card of switches for hardware that needs PadForge to do more than read a standard report. Each is off by default unless noted.

### Flydigi Enhanced Protocol

Turns on the extra buttons and motion sensors that supported Flydigi controllers expose beyond their standard gamepad report.

If Flydigi's own software is running, PadForge says so on the device. Both can hold the same controller interface, and a Vader 5 Pro has been reported dropping and reconnecting repeatedly while both had access. Stop the Flydigi service while using PadForge, or keep the controller hidden and take the service out of the HidHide whitelist.

### Read Logitech G-Keys

Reads the G-keys and extra mouse buttons on Logitech gaming gear through the vendor's G-key SDK, and presents them as their own device row. See [Logitech G-Keys](https://padforge.org/docs/features/logitech-g-keys/).

The SDK ships with **Logitech Gaming Software 8.55 or later**, not with G HUB. The status line under the switch says which step is missing: no SDK on the machine, a registered SDK whose file is gone, an SDK that was found and would not load, a library that loaded but is not the G-key SDK, or an SDK that refused to start because Logitech Gaming Software is not running. Once it connects, the line counts key events.

Two steps happen in Logitech Gaming Software, not in PadForge. Drag the **G-key** command onto each key in the PadForge profile there, or the key never reports. Then make that profile the **persistent profile**, or the keys only reach PadForge while its window is in front. Logitech's profiles have nothing to do with PadForge's own [profiles](../guides/profiles.md).

### Read Analog Keyboards

Reads how far every key is pressed on analog and Hall effect keyboards, and gives each supported keyboard its own device row. See [Analog Keyboards](https://padforge.org/docs/features/analog-keyboards/) for the keyboards it reads.

Most keyboards are read over the channel their own configurator uses, so close the configurator while this is on. Razer keyboards report only while Razer Synapse runs, and a Razer keyboard waiting for Synapse says so on its own row's panel on the Devices page. The status line under the switch says when no supported keyboard was found.

### Read Bliss-Box Adapters

Talks to Bliss-Box adapters through their API, so PadForge knows the controller in each port, reads a DualShock 2's pressure, rumbles through the adapter's own motor commands, and offers the Dreamcast screen, Controller Pak and player number actions in the port's panel on the Devices page. See [Bliss-Box Adapters](https://padforge.org/docs/features/bliss-box/).

Close the Bliss-Box API Tool and DeviceBuddy while this is on, since they talk to the adapter over the same channel. While it is on, each port reads in the adapter's own layout instead of through SDL's gamepad mapping, so map a port again after turning it on or off. Each port's row names the controller in it. The status line under the switch says when no port was found.

### Handheld PC Buttons

**Enable Handheld PC Buttons** is off by default, and off means nothing runs. On, the [Devices](devices.md) page gains a row for the paddles, menu keys and hotkeys a handheld gaming PC or laptop hides from games, learned by pressing each one once, and a row for the machine's own gyroscope when Windows reports one. See [Handheld PC Buttons](https://padforge.org/docs/features/handheld-buttons/).

### Keyboard Screens

**Drive Keyboard Screens** is off by default. On, PadForge draws on the screens built into Logitech's GamePanel keyboards and the G19, on a phone through Logitech Arx Control, and on the Mad Catz S.T.R.I.K.E. 7's touchscreen, and the [Devices](devices.md) page gains a row for each screen. A screen's row sets what it shows and carries the keys beside it. Turning the switch off gives a G19 screen PadForge bound to WinUSB back to Windows. See [Keyboard Screens](https://padforge.org/docs/features/keyboard-screens/).

While the engine runs, the row names only what the feature as a whole is missing: the Arx Control or S.T.R.I.K.E. 7 library, or any screen at all. **Download Strike7API.dll**, **Choose Strike7API.dll…** and **Install Visual C++ 2008 Runtime** get the S.T.R.I.K.E. 7's library and the runtime it needs.

### Novint Falcon

**Drive Novint Falcon** is off by default. On, PadForge opens each Novint Falcon through Force Dimension's SDK, which you install from Force Dimension, and the [Devices](devices.md) page gains a row for each one. An open Falcon belongs to PadForge alone, so FalconJoyForce and Force Dimension's own tools cannot open it, and turning the switch off closes every Falcon. See [Novint Falcon](https://padforge.org/docs/features/novint-falcon/).

While the engine runs, the row names the library that loaded, and when no Falcon opened, why. Each open Falcon's line and its **Calibrate Reach** are on its row's panel on the Devices page. **Choose dhd64.dll…** loads a copy of Force Dimension's library you pick in place of any other, within a few seconds, and a Falcon open on another copy opens again on it. **Install Falcon Driver** shows for a Falcon with no driver and installs Force Dimension's from the SDK folder. **Get Force Dimension’s SDK** opens Force Dimension's download page.

---

## Drivers

One card with a row per driver: **HidHide**, **HIDMaestro**, **BthPS3 Bluetooth**, **Windows MIDI Services** and **SteamVR**. Each row starts with a dot (filled when installed, an outline when not). HidHide also shows the installed version, and HIDMaestro and BthPS3 the version PadForge carries.

- **HidHide**: "Hides physical devices from games to prevent double input." **Install** and **Uninstall** buttons.
- **HIDMaestro**: "Creates the virtual controllers." The installed state and an **Uninstall** button. On Windows on ARM the state is that of usbip-win2's virtual USB host controller, which HIDMaestro installs there in place of its own driver. The driver installs itself the first time you create a virtual controller and is required for every Xbox / PlayStation / Nintendo / Extended / VR controller. **Uninstall** shows what HIDMaestro left on the PC, then stops the engine and removes it. See [Driver Management](driver-management.md#uninstall).
- **BthPS3 Bluetooth**: "Connects PlayStation 3 controllers over Bluetooth." The installed state and an **Uninstall** button. BthPS3 lets PlayStation 3 controllers, the PlayStation Move and the Navigation controller connect over Bluetooth, and installs itself the first time one of them is paired. **Uninstall** shows what BthPS3 left on the PC, then stops the engine and removes it. See [Driver Management](driver-management.md#bthps3).
- **Windows MIDI Services**: "Virtual MIDI ports and MIDI input." The API PadForge uses (**Built into Windows**, **App SDK Runtime** with the runtime's version, or **Legacy MIDI API**), or **Not Running** with the reason: the Windows MIDI service did not respond, a MIDI driver did not respond, or the runtime's uninstaller is still running. Under **Legacy MIDI API** the row names the error when the API built into Windows is registered but did not start. A Windows MIDI Services API that did not start when PadForge first checked is taken later, once it starts, with nothing restarted. **Install** shows on Windows 11 24H2 or later while Windows has no API of its own and no runtime is installed, and installs PadForge's build of the Windows MIDI Services runtime. **Uninstall** shows while a runtime is installed. See [Driver Management](driver-management.md#windows-midi-services).
- **SteamVR**: "Runtime for virtual VR controllers." Needed only for [VR controllers](https://padforge.org/docs/features/vr-controllers/). It describes itself as *VR runtime for virtual VR controllers. Installs from Valve’s servers with no Steam account or Steam client needed (several GB).*

PadForge is already elevated from its startup UAC prompt, so the Install / Uninstall buttons run their installer in the same elevated session without a second prompt.

### Driver summary

| Driver | What it does | When to install |
|---|---|---|
| **HidHide** | Hides physical controllers from games so they only see the virtuals. Stops double input. | Games see both the physical and the virtual. |
| **HIDMaestro** | Single user-mode driver that creates Xbox, PlayStation, Nintendo (Switch Pro and Switch 2 Pro), and Extended (DirectInput) virtual controllers, and the virtual keyboards and mice of a Keyboard + Mouse controller. 344 device profiles, of which PadForge offers 246 that it can deploy. Replaces ViGEmBus and vJoy in v3. | Installs itself the first time you create an Xbox, PlayStation, Nintendo, Extended or VR controller, or set a Keyboard + Mouse controller to a keyboard or mouse model. Required for those five types and those models. |
| **BthPS3** | Bluetooth drivers for PlayStation 3 controllers, the PlayStation Move and the Navigation controller: a profile driver and a filter on the PC's Bluetooth. | Installs itself the first time one of those controllers is paired for Bluetooth. |
| **Windows MIDI Services** | Gives each MIDI controller a port of its own and reads MIDI inputs. Part of Windows 11 25H2 from the late-November 2026 update. Without it, MIDI runs on the legacy MIDI API, where each MIDI controller sends to a port you pick. | On 24H2 or later before Windows carries the API, click **Install** on its row for the runtime. |
| **SteamVR** | Valve's VR runtime. PadForge fetches it directly, with no Steam account and no Steam client. | You want a [VR controller](https://padforge.org/docs/features/vr-controllers/). The VR type stays unavailable without it. |

### Uninstall guards

PadForge blocks four driver uninstalls until you clear what still depends on them.

- **HidHide** stays locked while any device still has the cloak armed. Turn off hiding on the [Devices](devices.md) page first.
- **Windows MIDI Services** (the runtime) stays locked while any controller still outputs MIDI through it. Delete those controllers or switch them to another type first. When the API built into Windows or the legacy MIDI API runs MIDI, the controllers do not hold the runtime.
- **SteamVR** stays locked while any VR controller exists. Delete those controllers or switch them to another type first.
- **BthPS3** waits while a PlayStation controller is connected over Bluetooth. Turn the controller off, then try again. Its drivers stay while DsHidMini is installed, since DsHidMini needs them.

The HidHide, MIDI and SteamVR guards read the controllers you have saved rather than what the engine is currently running, so they hold with the engine stopped, which is when most people go tidying up drivers. Switching a controller's type releases the old type's driver and locks the new one straight away.

### The SteamVR row

![The SteamVR row, installed](https://padforge.org/docs/images/settings-steamvr.png)

Before installing, the SteamVR row offers an **Install Location**. It defaults to `C:\SteamVR` and takes any full path on any drive. A bare drive root is refused, because the uninstall side deletes the recorded directory and pointing that at an entire drive is not a mistake worth allowing.

**Uninstall appears only for an install PadForge itself created.** PadForge works that out by checking whether it recorded the path and whether Steam's own registry entries point at the same folder. A SteamVR installed through the Steam client belongs to Steam, shows no Uninstall button here, and is never touched. The uninstall also stays disabled while any VR controller exists, and refuses while SteamVR is running.

SteamVR keeps its own log and settings inside the folder you chose, as `logs` and `config` subfolders. Removing the install takes them with it.

When another OpenVR driver is registered under HIDMaestro's driver name, the row warns: "Another OpenVR driver uses HIDMaestro’s driver name, so SteamVR can load it in place of PadForge’s and the virtual VR controllers will not appear. Remove that driver to clear this:" and lists that driver's folders, one per line. OpenVR loads one driver per name, so the VR controller's hands would otherwise fail with nothing else to show for it. PadForge takes the list from HIDMaestro's `HMVR.FindDriverNameConflicts` (HIDMaestro 1.12.0), which reads SteamVR's driver registrations and changes nothing, at most every ten seconds.

### HidHide Options

While HidHide is installed, a **HidHide Options** fold sits at the foot of the Drivers card. Folded, its hint reads "Hide Devices from Games, Keep Devices Cloaked Between Launches, Whitelisted Applications".

#### Hide Devices from Games

Master switch for input hiding. Two pieces sit under it.

- **HidHide cloaking** makes physical controllers invisible to games at the OS level.
- **Input hook suppression** stops mapped raw inputs reaching other input APIs.

On (default) means the per-device hiding switches on the [Devices](devices.md) page do what they say. Off forces every controller visible no matter what the per-device switches are set to. Use it as a panic button.

#### Keep Devices Cloaked Between Launches

Off (default) clears every HidHide cloak when PadForge exits, so other sessions see your controllers normally.

On leaves the cloaks in place between launches. Pick this when another app (Steam launching after PadForge exits, for example) scans for pads while PadForge is closed and you want it to keep seeing the physical controllers as hidden. The next PadForge start re-asserts the cloaks with no visible flicker.

Turning **Hide Devices from Games** off still decloaks right away, no matter what this is set to.

#### HidHide Whitelisted Applications

**Whitelisted Applications** picks which apps can still see hidden controllers: "These applications can still see hidden devices. PadForge is always whitelisted automatically."

You may also want to whitelist:

- Other controller-config tools
- Emulators that read raw hardware
- Diagnostic apps like joy.cpl

| Button | Action |
|---|---|
| **Add…** | Browse for an .exe. That program sees physical controllers even when hidden. |
| **Remove** | Drops the selected entry. PadForge is whitelisted automatically and never appears here, so there is nothing of its own to preserve. |

![Driver management cards](https://padforge.org/docs/images/settings-drivers.png)

![HidHide whitelist configuration](https://padforge.org/docs/images/settings-hidhide.png)

For full driver detail see [Driver Management](driver-management.md).

---

## MIDI Output

When a [virtual controller](controller-slots.md) is a **MIDI** controller, its page's first section, **Notes & Controls**, shows a **MIDI Output** card under the picture:

| Setting | What it does |
|---|---|
| **Channel** | MIDI channel (1-16). |
| **CC Count** | Number of Control Change outputs. How many axes send CC. Clamped so the highest CC stays within range, so the max is 128 minus **Start CC** (128 only when Start CC is 0). |
| **Start CC** | Base CC number (0-127). Axis CCs count up from here. |
| **Note Count** | Number of Note outputs. How many buttons send Note messages. Clamped so the highest note stays within range, so the max is 128 minus **Start Note** (128 only when Start Note is 0). |
| **Start Note** | Base note (0-127, 60 = Middle C). Button notes count up from here. |
| **Velocity** | Note On velocity (1-127). Higher = louder. |

Channel stands on the card, and the counts, starts and velocity open under its **More**. Under Windows MIDI Services, PadForge creates one virtual MIDI device per controller, with no port to pick. Under the legacy MIDI API the card adds **Output Port**, and the controller sends to the existing port picked there. Different controllers can target different channels.

---

## Community Configs

![The Community Configs card in Settings](https://padforge.org/docs/images/settings-community-configs.png)

Controls the [Steam Workshop Config Import](https://padforge.org/docs/guides/steam-workshop-import/) feature: browsing community-made Steam Input controller configs and translating them into PadForge profiles.

### Enable Community Configs

The master opt-in, off by default. PadForge sends nothing to Steam until you check it.

With it on, PadForge connects directly to Steam, and only when you act (a search, opening a config, the update check below, or getting a game's picture for a [theme](https://padforge.org/docs/features/themes/)). The servers are all Steam's own: store.steampowered.com, api.steampowered.com and the connection servers it names, steamcommunity.com, cdn.steamusercontent.com, cdn.cloudflare.steamstatic.com, and avatars.fastly.steamstatic.com. PadForge sends your search text, the ids of the games, configs and creators involved, and its version number. The anonymous Steam logon also sends the OS type, a language, and a machine id built from SHA-1 hashes of the Windows machine GUID, the physical network adapters' MAC addresses and the boot disk serial. No telemetry, no third-party service, no Steam sign-in. [Steam Workshop Config Import](https://padforge.org/docs/guides/steam-workshop-import/) says what each server receives.

The **Browse Community Configs** button on the [Profiles](../guides/profiles.md) page works either way. With the opt-in off, its dialog opens on an explanation panel and offers the enable step there.

### Show Legacy Workshop Configs

Appears once the opt-in is on. Configs from before 2017 have no downloadable file on Steam's servers, so PadForge lists them with a **Legacy** badge and reads them through your Steam install instead. Off by default. See [Legacy configs](https://padforge.org/docs/guides/steam-workshop-import/#legacy-configs).

### Clear Cached Configs

Empties the local Workshop cache (search results, config files, artwork) at `%LOCALAPPDATA%\PadForge\SteamWorkshopCache`. No confirmation, and nothing of value is lost: the cache refills as you browse. Imported profiles live in `PadForge.xml`, not the cache, and survive a clear. The message line confirms with "Workshop cache cleared".

### Check Imported Profiles for Updates

Asks Steam whether any config you imported has been updated since the import, in one query.

| Outcome | What you see |
|---|---|
| Everything current | Message line: "Imported profiles are up to date (3 checked)". |
| Updates found | A **Workshop Updates** dialog lists each stale profile and its config, with **Browse Community Configs** as the re-import route. |
| No imported profiles | Message line: "No profiles imported from the Steam Workshop yet". |
| Opt-in off | Message line: "Enable Community Configs to check for updates". Nothing is sent. |

---

## Settings File

Every controller, mapping, deadzone, [profile](../guides/profiles.md), [macro](../guides/macros.md) and preference lives in one XML file: `PadForge.xml`.

### Location

The full path shows at the top of the card. Use it to back up, copy between machines, or share for bug reports.

### Buttons

| Button | Action | When to use |
|---|---|---|
| **Save** | Writes the in-memory config to disk. | Force-save, or confirm changes hit disk. |
| **Reload** | Throws out in-memory changes, re-reads from disk. | Undo since last save, or pick up an external edit. |
| **Open Folder** | Opens the config folder in Explorer. | Backup or sharing. |
| **Reset to Defaults** | Restores factory defaults. It stands apart at the line's end in a warning color. | Last resort for a broken config. No undo. |

If unsaved changes exist, an **Unsaved Changes** warning sits under the buttons.

### Auto-save

Most settings auto-save on their own. A change reaches the engine within about 250 ms, and the file on disk is written after about 2 seconds of quiet, one write per editing burst. You do not need to click **Save** after every change.

The **Save** button is there for two cases.

1. Force-save before a risky operation.
2. Batch-save after a rapid run of changes.

---

## Diagnostics

*Version information, plus engine event logging for troubleshooting.*

![The Diagnostics card in Settings](https://padforge.org/docs/images/settings-diagnostics.png)

### Version info

| Field | Description |
|---|---|
| **App Version** | PadForge's version and build, for example `4.5.3 (r3682@176208e)`: the version, the commit count and the short commit hash. A build made without git shows the version alone. |
| **.NET Runtime** | .NET runtime in use |
| **SDL Version** | SDL3 library version |

Paste these into any issue you file.

### Keep a Diagnostics Log

Off by default. A healthy install writes no log.

Turning it on writes engine events continuously to `diagnostics.log` in the folder PadForge runs from, which **Open Log Folder** opens and names in its tooltip. The setting persists, so it survives a restart and covers sessions that start with Windows. That matters because a launch-time environment variable cannot reach an auto-started session, which is the case this was built for.

At 8 MB the file rolls over to `diagnostics.log.old`, so leaving it on for days costs one file plus its rollover. Each launch writes a marker line, so several sessions in one file stay easy to tell apart.

### Record a Problem

**Start Recording**, make the problem happen, then **Stop Recording**. The recording opens for you to read, part by part, before **Save…** writes it where you pick. Under the card's **More** are **What to Record**, **Keep the Last 60 Seconds** and **Write Typed Letters and Numbers as One Key**. [Problem Recorder](problem-recorder.md) covers every part, the review, memory dumps and sending a file privately.

### Save a Snapshot

**Save a Snapshot…** takes PadForge, Windows, your devices and settings as they are now, with the engine's most recent events, and opens them through the same review as a recording.

PadForge always keeps its recent engine events in memory, whether or not the log is on, so this captures the moments around something that just happened without having had logging enabled beforehand. Use it right after a glitch.

### Open Log Folder

Opens the folder that holds `diagnostics.log` in Explorer. It sits beside **Keep a Diagnostics Log**, and its tooltip names the folder. A program working through [Automation](automation.md) can press it only after you allow it on PadForge's question card, as for anything that opens a file or a folder.

---

## Related pages

- [Installation](https://padforge.org/docs/start/installation/): first-time setup.
- [Home](dashboard.md): engine status and overview.
- [Controller Slots](controller-slots.md): add and manage virtual controllers.
- [Devices](devices.md): physical controllers and assignments.
- [Driver Management](driver-management.md): driver install detail.
- [Themes](https://padforge.org/docs/features/themes/): the theme gallery, theme files and the builder.
- [Profiles](../guides/profiles.md): per-app config and Profile Shortcuts.
- [Profile Import](../guides/profile-import.md): bring in reWASD, DS4Windows and DSX profiles.
- [Steam Workshop Config Import](https://padforge.org/docs/guides/steam-workshop-import/): the feature behind the Community Configs card.
- [Troubleshooting](../troubleshooting.md): more help.

---

*Last updated for PadForge 6.0.0.*

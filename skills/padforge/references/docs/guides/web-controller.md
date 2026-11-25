<!-- https://padforge.org/docs/guides/web-controller/ as PadForge's skill carries it, from padforge.org's wiki/guides/web-controller.md. -->
# Web Controller

*Any device with a web browser becomes a game controller for your PC.*

Open the address PadForge shows on its **Web Controller** page from a phone, tablet, or another networked device. The tab shows up on the [Devices](../features/devices.md) page as a real input device, ready to assign to any [slot](../features/controller-slots.md).

Useful for an extra controller, a phone as a second pad, or touchscreen play on a tablet. The pages take the colors of the [theme](https://padforge.org/docs/features/themes/) PadForge wears when they load.

<figure class="pf-video">
<video controls playsinline preload="none" width="1920" height="1080"
       poster="/assets/video/padforge-tutorial-web-controller-poster.jpg"
       aria-label="Video: a browser as a controller, recorded live in PadForge 5.2.1">
<source src="https://padforge.org/assets/video/padforge-tutorial-web-controller.mp4" type="video/mp4">
<track kind="captions" srclang="en" label="English" src="https://padforge.org/assets/video/padforge-tutorial-web-controller.vtt" default>
</video>
<figcaption>The server turned on, its address opened in a browser on the same PC, PadForge's certificate accepted once, the Xbox 360 layout assigned to a virtual controller and its stick dragged, recorded live in PadForge 5.2.1 in 1 minute 19 seconds. PadForge 6 has since rebuilt the interface, so the screens differ from the steps on this page. The narration is a generated voice, with English captions. <a href="https://padforge.org/assets/video/padforge-tutorial-web-controller-transcript.txt">Read the transcript</a>.</figcaption>
</figure>

---

![Web controller landing page](https://padforge.org/docs/images/web-landing.png)

## Setup

### 1. Turn on the server

1. Open **Extras** in the sidebar and click the **Web Controller** tile, in the **More Ways to Play** group.
2. Turn on **Enable Web Controller Server** at the top of the page, or the switch on the tile.
3. The **Connect a Phone** card shows a QR code, three steps and the address, for example `https://192.168.1.100:8080`, with **Copy Address** beside it. When the certificate cannot be bound the address starts with `http://` instead, and a line under it says why. While the server does not serve, the address's place says why, such as a port in use.
4. The **Phones** card beside it names each phone connected now. With none it reads *No phones connected.*

While the server is on, [Home](../features/dashboard.md) shows a Web Controller tile whose **Show QR** button opens this page.

### 2. Open the URL on your phone

1. On the phone, tablet, or other device, scan the QR code with the camera, or open any web browser and go to the address PadForge shows.
2. The phone's home page loads with the layout choices.

### 3. Pick a layout

The home page has a **Your Name** field. A name typed there stays in that browser. The gamepad layouts, **Browser Gamepad** and your own pads send it, so a streamer with several phones connected can tell them apart on the Devices page. It holds up to 24 characters.

Under **Pick a Layout** stand four tiles: **Xbox Series X|S**, **DualSense**, **Switch Pro** and **Touchpad**. **More Layouts** opens the other ten: **Xbox 360**, **Xbox One**, **DualShock 4**, **DualSense Edge**, **Switch 2 Pro**, **Steam Deck**, **Steam Controller**, **Browser Gamepad**, **Web Menus** and **Build Your Own**. Each pad you saved adds its own tile there. That is fourteen built-in layouts in all.

A tap on a tile picks it, and the **Continue** button opens it. Continue names the pick, as in **Continue with DualSense**, and the phone remembers the layout and finish it opened last, so the next visit offers them first with the note *Last used on this phone*.

- The touchscreen gamepad layouts use the same 2D controller art the desktop app shows. Tap a trigger and its fill snaps to full.
- Layouts with a touchpad add a drag surface on the controller art, with its own click pill beside it.
- Touchpad is a multi-touch surface that drives the DS4 touchpad on whichever PlayStation slot it is assigned to.
- Web Menus shows a slot's Touch Grid menus as tiles. See [Web Menus](#web-menus).

### 4. Assign the controller to a slot

1. The browser controller shows up on the [Devices](../features/devices.md) page named for the layout you picked: **Xbox 360 Web Controller 1**, **DualShock 4 Web Controller 1**, **Web Touchpad 1**, or **Web Menus 1** (each layout numbers its own devices starting at 1).
2. Assign it to a controller slot on the Devices page, the same as any physical controller. A phone that sent a name shows it after a colon, as in **Xbox 360 Web Controller: Sam**.
3. Done. Start playing.

With [Remote Link](remote-link.md#4-share-and-assign), the web controller also appears on paired PCs. Assign it to a slot on the PC running the game. [Remote assignment](remote-link.md#assign-devices-from-the-other-pc) lets a streaming PC change those assignments after the gaming PC grants permission. A Web Menus phone is not shared, since its taps fire the menus of the PC it connects to.

> **Tip:** Use your browser's "Add to Home Screen" option for fullscreen mode without the address bar.

---

## Network requirements

The web controller is built into PadForge. Nothing extra to install. The phone or tablet and the PC must be on the **same local network** (the same Wi-Fi, usually), or reach the PC through an HTTPS tunnel or reverse proxy on the [plain HTTP address](#plain-http-address).

| Requirement | Details |
|-------------|---------|
| **Port** | TCP 8080 (default), set as **Web Controller Port** on the Web Controller page, under **More**. 1024 to 65535. |
| **Firewall** | Every time the server starts, PadForge rewrites its "PadForge Web Controller" inbound rule to allow the port in use. The [plain HTTP address](#plain-http-address) gets a rule of its own, "PadForge Web Controller (HTTP)", which PadForge removes when that address stops or is set to This PC Only. Third-party firewalls may need a manual TCP allow rule for each port. |
| **Max clients** | 16 browser tabs at once |

**Can't reach the URL?**

- Confirm both devices share the same Wi-Fi network
- Confirm the firewall allows port 8080
- Try a different port if 8080 is already in use. While the switch is on and the engine runs, PadForge checks the port again every 3 seconds and starts the controller once the other program lets it go

Stopping PadForge stops the server.

---

## Controller layouts

The touchscreen gamepad layouts draw the same 2D controller art the desktop app shows. Tap a trigger and its fill snaps straight to full. Keep the finger down and drag it downward to feather the pull, and slide back up for full again.

| Layout | What it adds |
|---|---|
| **Xbox 360** | Classic layout, dual analog sticks |
| **Xbox One** | Xbox One S layout |
| **Xbox Series X\|S** | Share button, hybrid D-pad |
| **DualShock 4** | Touchpad and lightbar |
| **DualSense** | PS5 layout with mute button |
| **DualSense Edge** | Fn buttons and back paddles |
| **Switch Pro** | Nintendo layout with Capture |
| **Switch 2 Pro** | C button and back paddles |
| **Steam Deck** | Dual trackpads and four grips |
| **Steam Controller** | Dual trackpads and paddles |
| **Browser Gamepad** | Forwards a controller detected by the browser |
| **Touchpad** | Multi-touch surface only |
| **Web Menus** | A slot's Touch Grid menus as tiles |
| **Build Your Own** | Drag widgets onto a blank pad |

Switch layouts at any time by going back to the home page.

### Finishes

Xbox 360, Xbox Series X|S, DualShock 4, and DualSense come in several finishes. Pick one of those layouts and a **Finish** row of color dots shows under the tiles. The Xbox 360 row has the default and **WLG**. Tap a dot, then **Continue**, and the layout opens with the art drawn in that finish.

### Lights, on the page

The layouts whose hardware has lighting draw it on the controller itself, where the real one is.

| Layout | What lights up |
|---|---|
| **DualShock 4** | The lightbar, front strip and rear glow both |
| **DualSense**, **DualSense Edge** | The lightbar around the touchpad, plus the player indicator row beneath it |
| Everything else | Nothing. An Xbox pad and a Switch Pro have no lightbar, so none is drawn |

Both follow whatever the slot is doing, so a game that drives the lightbar drives it here too, and the indicator row lights the same symmetric pattern a DualSense shows: one LED in the middle for player one, the outer pair for player two, outward from there.

A web DualShock 4 or DualSense is configurable from the controller page's **Lights & Sound** section exactly like a physical one (see [Lighting](../features/lighting.md)). Pick a color, a mode, or an animation and the phone shows it. Animated modes run the same engine the hardware pads use rather than a still approximation of it.

![The DualSense layout](https://padforge.org/docs/images/web-dualsense.png)

### Trackpads and grips

The Steam Deck carries both trackpads as real touch surfaces, plus its four rear grip buttons as tiles at the outer edges of the shell.

The 2015 Steam Controller is different, because that is how SDL maps the real hardware. Its left pad is an 8-way D-pad surface, its right pad is the right stick, and it has one physical thumbstick (the left one) plus two rear grips. The two pad-click zones are suppressed on glass, since they would sit on top of the surfaces and steal every touch.

![The Steam Deck layout](https://padforge.org/docs/images/web-steamdeck.png)

### Nintendo layouts

The Switch Pro layout carries the Capture button, and the Switch 2 Pro adds the C button and its two back paddles.

![The Switch 2 Pro layout](https://padforge.org/docs/images/web-switch2pro.png)

### Touchpad

A multi-touch surface that maps straight to the DualShock 4 / DualSense touchpad on the assigned PlayStation slot. Use it as a second input device when a phone or tablet is already in another player's hands and the DS4 / DS5 touchpad is the missing piece. No buttons or sticks, touchpad input only.

The same surface also drives every [touchpad](../features/touchpad.md) feature of the slot it is assigned to:

- **Mouse Output**: relative cursor control. The **Response** row's **Trackpad** mode slows fine positioning and speeds up fast drags, an acceleration curve ported from libinput.
- **Absolute Pointer**: the Touchpad Pointer sources warp the cursor to where your finger sits on the pad.
- **Stick / D-Pad Output**: a virtual analog stick or D-pad.
- The gesture stack: swipes, taps, shapes, and the rest.

Each slot the phone is assigned to carries its own toggles and tuning.

![Web controller in use](https://padforge.org/docs/images/web-controller.png)

---

## Build your own layout

**Build Your Own** opens a builder that starts from a blank pad. Tap **Edit** in the lower left corner to bring up the tools, a panel its grip moves, which fades while you drag a control. **+ Add widget** adds buttons, sticks, trigger sliders, a D-pad or a touch area, and a new one lands in the free place nearest the middle. Drag each where your thumbs sit, resize it, name the pad, and **Save**. **Grid** snaps to a square grid, **Remove** takes out the selected widget, and **Delete pad** deletes the pad. A pad holds up to 64 widgets.

A control keeps its shape on any screen. Its size is measured against the screen's shorter side, so a stick and a D-pad stay square and a button stays a circle or a pill. Controls that sit together move as a group: a group on the left keeps its distance from the left edge, one on the right from the right edge, one at the middle stays there, and every group keeps its distance from the bottom. Where groups would meet or leave the screen, the whole pad is drawn smaller.

A pad can be played held sideways or upright. It keeps one arrangement for each way of holding the screen. The first edit made on a screen held the other way gives the pad its own arrangement for it, and **Auto-fit**, shown while the pad has both, gives that arrangement up and fits the other one again. A pad saved before PadForge 6.0.0 has no screen shape stored, and the page works one out from its round controls.

Saved pads live in PadForge's own settings file beside the executable, machine-wide rather than inside a profile, and each one gets its own tile under **More Layouts** on the home page. A saved pad shows up on the [Devices](../features/devices.md) page under its own name, and its shape follows its widgets: a pad built from two buttons and a stick offers exactly that in the mapping picker.

![The custom controller builder](https://padforge.org/docs/images/web-custom.png)

---

## Browser Gamepad: a controller paired to the phone

Pick **Browser Gamepad** on the home page and the phone's browser reads a controller that is paired to the phone, or built into the handheld the page is open on, and forwards it to PadForge as its own device, named **Browser Gamepad 1**, then 2 for a second pad. Nothing is installed on the phone and nothing is paired to the PC.

Press a button on the controller first. Browsers hide a connected controller from every page until you press something on it, so the page shows a prompt until then. After that press the pad appears with its name, its layout, and its button and axis counts, and PadForge sees it on the Devices page. Assign it to a slot like any other controller.

What comes through depends on how the browser recognized the pad.

- **Standard layout**: the browser mapped the pad to the standard gamepad layout. Both sticks, both analog triggers, the D-pad, the face buttons, the bumpers, the stick clicks, Back, Start and Guide land on their PadForge slots. Buttons beyond the standard seventeen land on the paddle and Misc slots in order, up to ten of them, and you map them like any other button.
- **Raw layout**: the browser did not recognize the pad. Then button *i* is PadForge button *i* (slot 16 is skipped, so 21 buttons at most) and axis *i* is axis *i* for the first six, nothing is guessed, and there is no D-pad hat. Each axis is forwarded as the browser reports it. PadForge notes where each axis sat when the pad connected and returns it there when the page goes quiet, so a trigger the pad reports as an axis releases to its own rest and not to a made-up center. The rest is sampled whenever forwarding connects or reconnects, so release every control while the connection is being established, or a held control is recorded as its rest. Controls within those limits are reachable, and you map them by hand.

Rumble goes to the pad itself when the browser can drive it (Android Chrome can), otherwise to the phone's vibrator where the browser offers one. iPhone Safari offers neither: it exposes no gamepad haptics and no vibration API, so a pad forwarded from an iPhone gets no rumble. Each pad's row on the page says where its rumble goes.

Keep the phone awake. On the https address the page asks the browser to hold the screen awake while a pad is forwarded, and the browser may still refuse. On the plain http address that request is not available, so set the phone's screen timeout long enough. PadForge checks in with the page once a second. If the phone sleeps or loses Wi-Fi mid-press, PadForge releases everything on that pad a few seconds after the page stops answering, so a button cannot stay held on the PC. Until the page answers again its input is ignored, and when it does PadForge asks it to resend everything it holds.

The same page works from a gaming handheld's own browser. An Android handheld or a Windows handheld running PadForge's page can hand its built-in controls to a PC on the same network. It is expected to forward the controls the handheld's browser exposes, within the limits above. Handheld hardware has not been tested here.

The browser samples the pad on its animation clock, typically about 60 times a second, and PadForge's own polling target for a plugged-in pad is far shorter. For most games that difference does not matter. For anything where timing is the point, it will. The web controller allows sixteen sessions at once, and each forwarded pad is one session. When a forwarded pad's last slot assignment is removed while a game is rumbling it, PadForge stops that rumble. That automatic stop covers web controllers only, and a physical Xbox pad or wheel unassigned mid-rumble may keep its output until the game changes it.

### Fullscreen

Where the browser allows a page to go fullscreen, which Android Chrome does, each page has a fullscreen button in its header or its top-left corner. Tap it to hide the browser's own bars, tap again or swipe down to leave. iPhone Safari does not offer element fullscreen, so the button does not appear there.

## Web Menus

Pick **Web Menus** on the home page and the phone shows a slot's [Touch Grid menus](menus.md) as pages of tiles. Tap a tile and its cell fires on the PC: the key, controller button, or macro the cell carries, and every mapping row, macro trigger, or shift layer that reads the cell. A phone propped beside the keyboard becomes a panel of your own commands for the game on the screen.

### Set it up

1. Open **Web Menus** on the phone. It shows up on the [Devices](../features/devices.md) page as **Web Menus 1**.
2. Assign it to the slot whose menus you want, the same way as any controller. Until then the page says **Not on a slot**. The phone has no buttons or sticks of its own, so assigning it beside the slot's controller adds nothing but its tiles.
3. In that controller's **Macros & Menus** section, select each Touch Grid menu the phone should show and turn on **Show on Web Controller**, under the menu's **More** fold. The switch appears on Touch Grid menus, and on a Radial Ring menu only while it is still on from the menu's time as a Touch Grid, so it can be turned off. Until one is on, the page says **No menus to show**.

Each marked menu is one page, and a tab strip across the top switches between them. A phone assigned to several slots shows the pages of every slot, and each tab then names its slot.

### What a tap does

A tile stays pressed for as long as the finger rests on it. A quick tap still holds it for a tenth of a second, so the PC sees it. The cell then fires the way the PC overlay fires it, and a macro cell's own trigger mode decides the rest: a **While Held** macro runs while the tile is held, and a **Toggle** macro flips on each tap.

A tile held through a profile switch stops firing until it is tapped again, so the new profile never inherits a press. If the phone sleeps or drops off Wi-Fi with a finger on a tile, PadForge lets go of the tile three seconds after the page stops answering, the check the [Browser Gamepad](#browser-gamepad-a-controller-paired-to-the-phone) page uses.

A tile whose cell names a **Toggle** macro lights while that macro is on, whether the tile turned it on or the macro's own trigger did.

The PC's menu overlay stays hidden for phone taps. A tap presses a cell without hovering it, so nothing appears over the game.

### Which menus show

A menu appears on the phone when all of these hold:

- It is a Touch Grid menu with **Show on Web Controller** on, and it is enabled.
- Its device scope is any device, or this phone.
- Its **Layer** is **Any Layer** or **Base**, or it names the shift layer that is active right now. The pages follow the layer as it changes.

A tile shows its cell's icon and, when the menu's **Show Labels** is on, its label. An emoji icon draws in color on the phone. Icon package entries and image files draw as pictures, fetched by an opaque token, so the page never learns a file's path. The grid takes the overlay's shape: 4 cells make 2 × 2, 12 make 4 × 3.

### Profiles from the phone

The top of the page names the PC's active profile. Tap it for the profile list, and pick a profile to switch the PC to it. That is a manual switch, the same one a [controller shortcut](profiles.md#controller-shortcuts) makes, and the switch flyout shows it the same way.

### Limits

- Remote Link does not offer a Web Menus phone to a paired PC in this version, since its taps fire the menus of the PC it connects to.
- Banner images, per-tile colors, and a hand-set row and column count are not part of the page. It keeps PadForge's tile style and the overlay's grid shape.

## Phone motion

The page can read the phone's own gyroscope and accelerometer and stream them to the slot as motion, so tilting the handset aims.

Browsers only hand out sensor readings over a secure connection. PadForge binds a self-signed certificate and serves the controller over **HTTPS** whenever that binding succeeds, which needs PadForge running elevated. If the binding fails it falls back to plain HTTP, and everything except the phone sensors and Browser Gamepad's keep-the-screen-on request still works. The **Connect a Phone** card then says why under the address. When another program's certificate holds the port, choosing another port brings HTTPS back.

Because the certificate is self-signed, the phone shows a one-time warning the first time it connects. Accept it and the connection is remembered.

Motion is off until you ask for it. When the page is served over HTTPS and the browser exposes the sensors, a **Motion** button appears in the bottom right corner of any gamepad layout. It turns ember while motion is sent. Tap it to start streaming and tap it again to stop. Browsers that ask permission for the sensors, Safari on iOS among them, ask on that tap, so there the button is required, not optional. The Touchpad page has no motion button.

Opened over plain HTTP on a touch device, a gamepad layout shows **Motion needs HTTPS** in that corner instead. When PadForge also serves HTTPS, the note reads **Motion: open over HTTPS**, and tapping it opens the same layout on the secure address.

The **QR code** on the Connect a Phone card gets the phone to the right address without anyone typing it.

## Plain HTTP address

Some browsers refuse PadForge's self-signed certificate outright, and a tunnel or reverse proxy that reaches the PC from outside brings a certificate of its own. For both, the Web Controller page can serve a second address over plain HTTP, on its own port, beside the main one. The main address does not change.

Turn on **Also Serve Plain HTTP** on the Web Controller page, under **More**. The plain address uses port `8081` unless you set another as **Plain HTTP Port**, and it cannot share the main address's port. While the server runs, the **Plain HTTP Address** row under it shows the address with **Copy Address** and its own QR code, or why the address is not served.

<!-- SCREENSHOT: dashboard-web-plain -->
![The More fold of the Web Controller page with Also Serve Plain HTTP on, the Plain HTTP Port, This PC Only, the Access Code with New Code, and the Plain HTTP Address with its QR code](https://padforge.org/docs/images/dashboard-web-plain.png)

### The access code

Every request to the plain address needs the page's **Access Code**, ten letters and digits. The address the page shows carries it, as in `http://192.168.1.100:8081/?code=7KQ2M9XH4P`, and so does the QR code beside it. The first page trades the code for a browser-session cookie, so the pages and the connection after it need nothing more. A browser that restores its last session can keep that cookie after it is closed and opened again, and **New Code** is what shuts it out. Opening the plain address without the code, or with a replaced one, gets a page saying the controller opens only with its access code.

**New Code** replaces the code and disconnects everyone on the plain address. Their pages stop working until they open the address with the new code. The main address and its clients are not affected, and the main address asks for no code.

The code is stored in `PadForge.xml` encrypted for this PC, so a copy of the file posted elsewhere carries no working code. Copied to another PC, the stored code cannot be read there, and PadForge makes a new one.

### This PC Only

**This PC Only** admits only this PC on the plain address and removes the plain port's firewall rule. It is for a tunnel or reverse proxy running on this PC, cloudflared, ngrok or Caddy for example, pointed at `http://localhost:8081`. Share the tunnel's public address with `/?code=` and the access code on the end. The page shows the local address with the code, and no QR code, since a phone cannot open `localhost`.

### The controller picture for OBS

The plain address also serves a read-only page that draws a slot's controller, lit from its live output, on a transparent background. It is meant for an OBS browser source, so viewers of a stream can watch the controller that [Twitch Chat](../features/twitch-chat.md) is pressing. The [Twitch Chat](../features/twitch-chat.md) page's **Picture for Your Broadcast** card shows its address once the plain address runs, as `/display.html?code=` and the access code. Use that address, not the main one. OBS's browser source refuses the main address because its certificate is self-signed. **This PC Only** is enough when OBS runs on this PC.

The page sends nothing and takes no client slot. Up to four open at once. See [The controller picture for OBS](../features/twitch-chat.md#the-controller-picture-for-obs).

### What plain HTTP gives up

Browsers expose the motion sensors only over HTTPS, so a layout opened on the plain address on a touch device shows **Motion needs HTTPS**, or **Motion: open over HTTPS** when PadForge also serves HTTPS. A tunnel or proxy that serves HTTPS makes the page secure again, so motion works there as long as the browser offers the sensors and the proxy carries the controller's connection as well as the page.

Plain HTTP is unencrypted. Anyone who can watch the network sees the input and the access code. Across the internet, use a tunnel or proxy that adds HTTPS.

The [Browser Gamepad](#browser-gamepad-a-controller-paired-to-the-phone) page's request to keep the screen on needs HTTPS as well, and a browser that blocks cookies loads the first page and nothing after it.

## Touch controls

| Control | Behavior |
|---------|----------|
| **Buttons** | Tap to press. Touch zones are larger than the visible art for easier targeting on small screens. |
| **Analog sticks** | Drag inside the stick zone to move. Release to re-center. **Stick click (L3 / R3):** tap and release within 200 ms with little movement. |
| **D-pad** | Touch and drag toward a direction. All 8 directions (cardinal and diagonal). |
| **Triggers** | Tap to send a full press. Hold and drag down to feather the pull, back up for full. Release to return to zero. |

---

## Connection and latency

The web controller stays connected for instant input. Updates send the moment you touch the screen.

| Status | Meaning |
|--------|---------|
| **Connected** | Active connection. Input reaches PadForge. A controller page shows it in a pill at the top's middle. |
| **Disconnected. Tap to reconnect.** | Connection lost. The message covers the page, and a tap reconnects. |

**Latency:** a Wi-Fi hop costs more than a wired controller does. Fine for casual, puzzle, and platform games. Fast competitive play may feel a touch less responsive.

**Reconnection:** each browser tab keeps a persistent pad ID. A brief disconnect (phone screen lock) does not change the device identity or slot assignment. No reassignment needed.

---

## Rumble feedback

PadForge forwards rumble to the browser, which uses the device's vibration support. It needs a vibration motor and a browser that offers vibration. Chrome on Android vibrates once you have touched the page. Safari on iOS offers no vibration, and other browsers vary. Rumble for a controller paired to the phone, through Browser Gamepad, is a separate path.

---

## The one-time refresh

Tap a gamepad layout and the page refreshes once before the controller appears. That happens on any browser, and it is expected. The controller works normally after it.

The refresh works around an iOS Safari bug. On iOS, the connection fails on the first load when the page is reached by tapping a link, and a reload clears it. Chrome, Firefox, Edge, and Chrome on Android do not have that bug, but they still do the single refresh. The builder does not do it at all. The Touchpad page does the same single refresh.

---

## Requirements summary

| Requirement | Details |
|-------------|---------|
| **Network** | PC and device on the same local network (Wi-Fi), or a tunnel or reverse proxy to the plain HTTP address |
| **Port** | TCP 8080 (default), not blocked by the firewall. The optional plain HTTP address adds TCP 8081 (default). |
| **Orientation** | Every built-in gamepad layout needs landscape and shows a "Rotate to landscape mode" warning in portrait. The Touchpad layout and a pad you build work in portrait or landscape. |
| **Browser** | Any current browser (Chrome, Safari, Firefox, Edge) |
| **Touch** | Touchscreen recommended for analog stick and multi-touch input |
| **Max clients** | 16 browser tabs at once |

---

## Related pages

- [Home](../features/dashboard.md): the Web Controller tile while the server is on.
- [Devices](../features/devices.md): see the web controller's row and assign it to a slot.
- [Twitch Chat](../features/twitch-chat.md): the controller picture for an OBS browser source.
- [Controller Slots](../features/controller-slots.md): create a virtual controller for the web controller to feed.
- [Troubleshooting](../troubleshooting.md): help with connection issues.
- [3D and 2D Visualization](https://padforge.org/docs/features/visualization/): the same controller art powers both the web controller and the desktop 2D view.

---

*Last updated for PadForge 6.0.0.*

<!-- https://padforge.org/docs/guides/remote-link/ as PadForge's skill carries it, from padforge.org's wiki/guides/remote-link.md. -->
# Remote Link

*Share a controller, wheel, or HOTAS between PCs, on your own network or across the internet. A device plugged into one drives a game on another, and the feedback comes back to the real hardware.*

![The Remote Link page under Extras](https://padforge.org/docs/images/remote-link.png)

Remote Link connects two PadForge PCs, on the same network or on opposite sides of the internet. A device plugged into one PC (the owner) shows up in another PC's PadForge (the consumer) as an ordinary input device, ready to assign to a [slot](../features/controller-slots.md) and map like anything else. The game on the consumer sees a virtual controller and never knows the hardware is in another room.

It works both ways at once, and across more than two PCs. Each PC can share its own devices and use the others' at the same time, and one shared controller can drive games on several PCs at once. Pairing is done a pair at a time, but a PC can be linked to many others.

---

## What comes back

Feedback travels the other way too. When the game on the consumer drives the shared device, that feedback returns across the link and plays on the physical hardware where it lives:

- Rumble (both body motors)
- Xbox trigger rumble (the impulse-trigger motors on Xbox One and later pads)
- Wheel force feedback (native on Logitech, Fanatec, and Thrustmaster, with other wheels and FFB joysticks driven through their DirectInput haptic effects, and rumble as the last fallback)
- DualSense adaptive triggers
- Lightbar color
- Player-number LEDs
- Guide button LED brightness (Xbox One and later pads on USB, the Home button LED on the 2015 Steam Controller, and the Switch HOME button LED on a Pro Controller or right Joy-Con)
- Controller speaker audio
- HD haptic tones (pads with no speaker that play macro sounds through their actuators: Joy-Con, Switch Pro, Steam Controller, Steam Deck)

A wheel shared from the den shakes in the den while the race runs on the PC in the office, and a DualSense lights its bar and buzzes its triggers on the couch while the game plays upstairs.

A shared device's **Device Dossier** on the [Devices](../features/devices.md) page shows the owner's firmware and revision text, because the copy has no open device to read them from. The copy of a DualShock 4 also learns whether the owner's pad is on a cable or on Bluetooth. That decides whether a first-model pad on a cable gets speaker audio, which it cannot play. An owner running a build before 5.3.0 sends none of this, and the copy's rows stay as they were.

---

## NFC tag taps

Tag reads travel with the input. Tap a tag on a shared Switch controller that has an NFC reader (a right Joy-Con, a Joy-Con pair, or a Pro Controller) and the tap fires [NFC tag](https://padforge.org/docs/features/nfc-tags/) triggers on the PC using the device, so a tag can start a macro there just like on a local controller.

- Binding a tag trigger is all it takes. The PC using the device reports the demand across the link, and the owner powers up the controller's reader.
- **Any NFC Tag** fires with no registration on either PC.
- A named-tag trigger matches by the tag's button number, and each PC numbers its own registered tags. Register the same tags in the same order on both PCs so the numbers line up.
- A standalone NFC reader shares across the link like any other device and brings its tag names with it, so its tags only need registering on the PC it is plugged into.
- The Switch controller must be on Bluetooth. A USB-linked Switch controller cannot read tags, shared or not.
- A gamepad-only pairing does not block tag taps. They still fire, and the macros they start skip their keyboard, mouse, and scroll actions.

---

## Set it up

### 1. Turn on Remote Link on each PC

Open **Extras** in the sidebar and click the **Remote Link** tile, in the **Send It Elsewhere** group. Turn on **Enable Remote Link Server** at the top of the page, or the switch on the tile. Each PC starts listening and begins announcing itself to the local network. The status card under the page's head says whether the server runs, with a flame that burns while it does, and why when it does not.

### 2. Find the other PC

On the same network, a PC running Remote Link shows up in the **Pair Another PC** card with its name and address. Click its **Pair** button. With none in sight the card reads *No unpaired PCs nearby.*

On different networks, or when discovery is blocked, use codes. The same card shows **This PC’s Code** once it is made, a dash-grouped string like `A7K2M-...`, with **Copy** beside it. Copy yours and send it to the other person over any chat. Paste theirs into **Connect by Address**, under the page's **More** fold. You both click **Pair / Connect**, and the two PCs find each other with no VPN and no port forward. A code lasts 24 hours and re-mints itself when this PC's public address moves, so copy it fresh at connect time.

The same box still takes a plain address. Type `192.168.1.20:27500` to name a host directly.

### 3. Pair with a six-digit code

Start pairing from one side. Both screens show a six-digit code. Check that the two codes match, then confirm on both. The code is derived from the key exchange itself, so a match rules out anyone sitting in the middle of it.

Pairing only happens once per pair of PCs. To add another PC, pair it the same way. A PC can hold many trusted peers at once.

### 4. Share and assign

Once paired, each PC's shareable devices appear in the other's [Devices](../features/devices.md) list. Assign a shared device to a [slot](../features/controller-slots.md) and map it like any local controller.

---

## Trust and reconnecting

### Assign devices from the other PC

Both PCs need a build with remote assignment support.

Web controllers travel over Remote Link like other input devices, and remote assignment lets you choose their slots from the PC sharing them. Web Menus is the exception: its taps fire the menus of the PC the phone connects to, so Remote Link does not share it. For a two-PC streaming setup:

1. On the gaming PC, open the Remote Link page, find the streaming PC in the **Paired PCs** card, and turn on **Allow Remote Assignment Changes** under its line. The same option is available during pairing. It starts off, including for existing pairings.
2. On the streaming PC, find the gaming PC under **Paired PCs** and click **Assign Shared Devices**. The button shows while that PC is connected.
3. Select a shared device, then check the virtual controllers it should feed on the gaming PC. Clear a checkbox to remove that assignment. One device can feed several controllers.

The dialog shows the destination PC and active profile. Changes use that PC's normal mapping and save process without opening its window or changing the foreground application. Other devices and assignments stay in place.

The grant covers the peer's own shared devices and this PC's existing virtual controllers. Create destination controllers locally before assigning them. This control only assigns and unassigns. It cannot switch profiles or edit mapping rows. Gamepad-only restrictions still apply. Turning the permission off blocks further changes and preserves assignments already made.

If the destination's settings change while the dialog is open, review the refreshed assignments before clicking again. If a request times out, click **Refresh** to check the actual state. A missing reply does not establish whether the change was applied. Reopen the dialog after reconnecting a PC.

### Automatic reconnect

Pairing records the other PC as trusted. After that, trusted PCs reconnect on their own the moment they find each other, on the local network or across the internet, with no code to re-enter. **Auto-Reconnect** is on by default and can be turned off under the Remote Link page's **More** fold.

Trust is tied to each PC's cryptographic identity, not its name. Renaming a PC does not break a pairing. The display name is only there so you can tell your paired PCs apart.

Discovery on the local network broadcasts this PC's name, its identity fingerprint, the protocol version and the link port to the subnet, and appearing in that list grants nothing. Paired PCs exchange device names, ids, types and capabilities alongside the input, rumble and audio they carry. Over the internet, PadForge asks public STUN servers for its outside address, uses the public BitTorrent DHT to meet a PC by code and to find paired PCs that moved, and falls back to n0's free iroh relays when no direct path exists. The DHT record carries the public signing key, and a relay sees public keys, nonces and signatures before the encrypted session starts. [Remote Link Internals](https://padforge.org/docs/reference/remote-link-internals/) names every server.

Each paired PC's line in the **Paired PCs** card has a flame that burns while it is connected, its name over its state, **Pair / Connect** while it is on this network and not connected, **Rename**, which puts the cursor in its name to type over it, and **Revoke**. Pointing at the name shows the PC's machine name and its key's fingerprint. An emptied name goes back to the PC's own.

To end a pairing, click **Revoke** on its line. **Revoke All**, at the card's head, drops every trusted PC at once. A revoked PC cannot reconnect until it pairs again.

---

## Staying safe: gamepad-only

A paired PC can drive this one's gamepad output. Whether it can also send keyboard, mouse, and scroll input here, directly or through macros, is your choice. The pairing dialog has a **Gamepad Only** checkbox. It starts off, so check it when you pair a PC you do not fully control. A PC paired that way says *gamepad only* in its state line. To change the setting for a PC you already paired, revoke it and pair again.

With gamepad-only on, a shared device cannot type, click, or scroll on this PC. A Keyboard + Mouse or VR controller it feeds sends nothing, and macros it triggers, or macros on a slot it feeds, skip their keyboard, mouse, scroll, and cursor actions. Their other actions still run, Run Program included, and a MIDI controller it feeds still plays.

---

## Identity protection

Each PadForge install has a Remote Link identity, the key that pairings trust. You choose how it is stored, in **Pairing Identity** under the Remote Link page's **More** fold:

| Mode | Stored as | Use when |
|---|---|---|
| Secure: This PC Only | Encrypted to this PC | The normal choice. The identity never leaves this machine, and any Windows user on it can use it. |
| Portable: Password Protected | Encrypted with a password you set | You want to carry one identity across installs. Pair once, then clone. |
| Portable: No Password | Plain, no password | A throwaway or test identity. |

A portable identity lets a copied install keep its pairings, since your paired PCs recognize the copy as the same PC. Two PCs running one identity at once do not see each other. Most people never need to change this from Secure.

---

## Network

Remote Link finds other PCs on your **local network** automatically. Discovery is a same-subnet broadcast, so the PCs being on the same Wi-Fi or switch is the usual local setup. Across the internet, codes replace discovery.

| Requirement | Details |
|---|---|
| Discovery | UDP broadcast on port 27501 (same subnet only) |
| Connection | Direct PC-to-PC on port 27500 by default, encrypted end to end. The port carries both a TCP listener (the handshake when you dial an address) and a UDP socket (the input and feedback stream, and hole punching). |
| Listening port | 27500 by default. Change it per PC as **Remote Link Port**, under the Remote Link page's **More** fold (any port from 1024 to 65535). The reset button next to the field restores 27500. |
| Internet reach | Outbound only. The public-address probe and the relay fallback both dial out, so no inbound rule and no port forward. |
| Firewall | Allow PadForge through the firewall on each PC |

If you write per-port firewall rules instead of allowing the whole app, open UDP 27501 for discovery and both TCP and UDP on the connection port (27500 unless you changed it) on each PC.

### How the internet path works

PadForge probes its own public address, folds it into the code, and punches a direct UDP path to the PC whose code you pasted. Both sides punch, which is why both people paste and both click **Pair / Connect**.

Where a direct path cannot exist, the link falls back to the free public relays run by n0.computer, reached over an outbound WebSocket. The relay forwards bytes without reading them. The handshake and session encryption are unchanged: the relay operator sees the handshake's public keys, nonces, and signatures, and ciphertext after that.

After the first pairing, codes are done. Each PC publishes its current endpoints under its long-term identity key, so a paired PC that moves to a new network is found and reconnects on its own.

Some networks cannot be punched at all. On a mobile hotspot or carrier-grade NAT, the **Pair Another PC** card says so under the code before you try, and names a VPN like Tailscale as the way around it. The relay still carries the link where the punch fails.

---

## How the security works

Pairing runs a fresh key exchange and signs the whole exchange with each PC's long-term identity key, so each side proves it is the same PC it paired with before. The six-digit code is derived from the exchange after both sides have fixed their contributions to it, which is why a matching code rules out a machine in the middle. A failed handshake creates no device and shares nothing. Traffic after pairing is encrypted.

---

## Limits

- **Discovery is local only.** It is a subnet broadcast, so it never crosses the internet. Reaching a PC on another network means swapping codes, or naming its address directly.
- **A code is not a password.** It authenticates nobody. The six-digit pairing check and the mutual key exchange are what gate trust, so a code someone else gets hold of still cannot pair with you.
- **Input devices only.** Remote Link shares the input devices PadForge reads (controllers, wheels, HOTAS, keyboards, mice, MIDI, NFC readers), including ones hidden from the [Devices](../features/devices.md) page, not arbitrary USB hardware. A device another PC shares with this one is not passed on.
- **Every PC runs PadForge.** Remote Link is PadForge-to-PadForge.

---

## Related pages

- [Devices](../features/devices.md): see shared devices and assign them to slots.
- [Controller Slots](../features/controller-slots.md): create the virtual controller a shared device feeds.
- [Force Feedback](../features/force-feedback.md): the rumble and force-feedback that returns over the link.
- [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/): the Xbox trigger-motor rumble that returns over the link.
- [Wheel](https://padforge.org/docs/features/wheel/): native wheel force feedback, including over Remote Link.
- [Lighting](../features/lighting.md): the lightbar, player LEDs, and Guide button LED that return over the link.
- [Controller Audio](https://padforge.org/docs/features/controller-audio/): the speaker audio that returns over the link.
- [NFC Tags](https://padforge.org/docs/features/nfc-tags/): the tag triggers a shared Switch controller or reader fires over the link.
- [Troubleshooting](../troubleshooting.md): help when a PC does not appear or pairing fails.

---

*Last updated for PadForge 6.0.0.*

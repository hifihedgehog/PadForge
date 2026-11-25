<!-- https://padforge.org/docs/start/first-controller/ as PadForge's skill carries it, from padforge.org's wiki/start/first-controller.md. -->
# Your First Controller

*From a fresh install to a game seeing a virtual pad. About five minutes,
and nothing here assumes prior knowledge.*

You need PadForge installed and running. If it is not,
[Installation](https://padforge.org/docs/start/installation/) covers that in two minutes.

<figure class="pf-video">
<video controls playsinline preload="none" width="1920" height="1080"
       poster="/assets/video/padforge-tutorial-first-controller-poster.jpg"
       aria-label="Video: your first virtual controller, recorded live in PadForge 5.2.0">
<source src="https://padforge.org/assets/video/padforge-tutorial-first-controller.mp4" type="video/mp4">
<track kind="captions" srclang="en" label="English" src="https://padforge.org/assets/video/padforge-tutorial-first-controller.vtt" default>
</video>
<figcaption>The install and the same steps in 1 minute 40 seconds, recorded live in PadForge 5.2.0, before PadForge 6 rebuilt the interface, so the screens differ from the steps below. The narration is a generated voice, with English captions. <a href="https://padforge.org/assets/video/padforge-tutorial-first-controller-transcript.txt">Read the transcript</a>.</figcaption>
</figure>

---

## 1. Plug in your pad

Connect the physical controller you want to use, wired or Bluetooth. Until a
virtual controller exists, [Home](../features/dashboard.md) is one page: a
card at the top names the controller PadForge sees and reads **Connected**.
With nothing plugged in it reads **No controller connected**. A Bluetooth pad
that is not paired yet pairs from **Pair a Bluetooth controller** at the foot
of the page, which opens the [Devices](../features/devices.md) page.

## 2. Pick what games should see

Under **What should games see?**, click **Xbox**. Almost every PC game with
controller support reads Xbox-style input natively, so it is what to pick
when you do not know what to pick. **PlayStation** sits beside it, for games
with PlayStation prompts, touchpad and lightbar, and **Keyboard + Mouse**
plays keyboard and mouse games with a pad. The line under the tiles links
the other four: Nintendo (a virtual Switch Pro or Switch 2 Pro Controller)
for games with Switch prompts, Extended for wheels, flight sticks and custom
layouts, MIDI, and VR for a SteamVR hand pair.

The click makes the controller and gives it the pad Home named. The
controller's entry appears in the sidebar under **Controllers**, and its page
opens.

![Add a Controller](https://padforge.org/docs/images/add-controller-popup.png)

Once a controller exists, Home shows the controllers instead. To add another,
click **Add Controller** in the sidebar, or its card on Home. **Add a
Controller** opens: pick the device under **Driven By**, or **Choose Later**,
then click a type under **Common** (Xbox, PlayStation, Nintendo) or **More
Outputs** (Keyboard + Mouse, Extended, MIDI, VR). The controller is added as
soon as you click.

Making the controller is the only step. The first time an Xbox, PlayStation,
Nintendo or Extended controller comes up, PadForge installs the HIDMaestro
driver by itself. There is no button to click.

## 3. See it working

The controller's page opens on its first section, **Mappings**, with the
**Picture** view. Press buttons and move sticks on your pad: the picture of
the controller follows. Because your pad is a recognized gamepad, its sticks,
triggers, buttons and D-pad were mapped 1:1 when the controller was made. The
controller is already playable.

Switch to **List** to see every row. Open a row, and its **Value** shows what
the game gets, live.

The pad shows under **Driven by** at the top of the page. To give the
controller another device, click **Add Device**, which opens the Devices
page. There, select the device and click the controller under **Used By**,
or drag the device onto the controller in the sidebar. A second click on the
controller under **Used By** takes the device off.

## 4. Launch a game

Start any game with controller support. It sees a standard Xbox pad.

One thing can go wrong here: if the game reacts twice to every press, it
is seeing both your physical pad and the virtual one. Install **HidHide**
from the [Settings](../features/settings.md) page, then on the
[Devices](../features/devices.md) page select your pad and turn on **Hide
from Games (HidHide)** in its **Settings** card. A pad assigned after HidHide
is installed has it turned on already. If you do not see double input, you
do not need it.

---

## Where to go next

- [A Five-Minute Remap](https://padforge.org/docs/start/five-minute-remap/) changes your first binding.
- The [Guides](https://padforge.org/docs/guides/) each walk one task end to end, from
  gyro aiming to using your phone as a controller.
- The [Features](https://padforge.org/docs/features/) pages document every control in
  every section.

---

*Last updated for PadForge 6.0.0.*

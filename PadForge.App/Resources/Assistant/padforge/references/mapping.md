# Inputs, targets and rows

## Inputs

A mapping row reads one input of one device. `get_device` and `wait_for_input` give each input in the form `set_mappings` takes:

| Input | Meaning |
|---|---|
| `Button 3` | Button 3, from 0 |
| `Axis 1` | Axis 1, the whole of its travel |
| `Slider 0` | Slider 0 |
| `POV 0 Up` | Hat 0 pushed up. `Down`, `Left` and `Right` work the same way |

Pass the device's short ID as `device`. Without it the row reads the controller's first device. `get_mappings` writes an input read the other way round with an `I` in front (`IAxis 1`), one half of an axis with an `H` (`HAxis 1`) and both with `IH`. `set_mappings` takes the plain input with `inverted` and `half_axis` instead.

## Targets

Each row of `get_mappings` has a `target`: the control of the virtual controller it drives, such as a button, a stick's axis or a trigger. Write rows by target. Rows you do not name stay as they are.

## Row options

- `inverted` reads the input the other way round.
- `half_axis` reads one half of an axis, for a trigger fed by a stick.
- `negative_source` drives the other direction of an axis row from a second input, such as two buttons for one stick axis.
- `dead_zone` is the share of the travel, in percent, that reads as rest.

## Automatic mapping

Assigning a known gamepad maps it at once. `auto_map` maps a controller's devices again the same way, which replaces the rows it writes.

## Macros

`list_macros` gives each macro's `data` in PadForge's own JSON form. To change one, change that data and pass it to `save_macro` with the macro's `index`. A new macro leaves `index` out. A macro that runs a program, opens a file or an address, or presses keys or moves the mouse asks the user first.

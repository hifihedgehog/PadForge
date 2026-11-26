# PadForge's tools by level

Generated from the tools PadForge serves. Each level allows what the ones above it allow. An argument with * is needed.

## Commands

| Tool | What it does | Arguments |
|---|---|---|
| `status` | PadForge's Status |  |
| `switch_profile` | Switch Profile | `profile` |
| `set_engine` | Start or Stop the Engine | `on*` |
| `set_controllers_power` | Turn Virtual Controllers On or Off | `on*`, `controller` |
| `record_problem` | Record a Problem. A recording needs Read. With seconds 0 it saves a snapshot at Commands. | `seconds`, `note` |

## Read

| Tool | What it does | Arguments |
|---|---|---|
| `list_profiles` | List Profiles | `profile` |
| `list_controllers` | List Virtual Controllers | `outputs`, `presets` |
| `list_devices` | List Devices | `kind`, `assigned`, `online`, `words` |
| `get_device` | A Device's Controls | `device*`, `live` |
| `wait_for_input` | Wait for the User's Input | `seconds`, `device`, `timeline` |
| `get_mappings` | A Controller's Mappings | `controller*`, `macros` |
| `find` | Find in PadForge | `words`, `kind`, `most` |
| `read` | Read a Place | `id*` |
| `where` | What the User Sees |  |
| `show` | Show the User Where | `place`, `words`, `advance`, `seconds`, `steps`, `walk` |

## Send Inputs

| Tool | What it does | Arguments |
|---|---|---|
| `add_device` | Add a Device of Your Own | `key*`, `name`, `buttons`, `axes`, `sliders`, `hats`, `gyro`, `accel`, `battery`, `touchpads`, `feedback` |
| `set_device_inputs` | Drive Your Device | `device*`, `buttons`, `axes`, `hats`, `hat_directions`, `rest` |
| `remove_device` | Remove Your Device | `device*` |
| `hold_control` | Hold or Release a Control | `device*`, `control*`, `value`, `release` |
| `set_value` | Hand a Value to Macro Triggers | `name*`, `value`, `release` |

## Change Settings

| Tool | What it does | Arguments |
|---|---|---|
| `set` | Change a Place | `id`, `value`, `values` |
| `press` | Press a Button | `id*` |
| `create_profile` | Create a Profile | `name*`, `copy_of` |
| `rename_profile` | Rename a Profile | `profile*`, `name*` |
| `delete_profile` | Delete a Profile | `profile*` |
| `import_profile` | Import a Profile | `path`, `file_name`, `content_base64` |
| `export_profile` | Export a Profile | `profile*`, `path` |
| `add_controller` | Add a Virtual Controller | `type*`, `preset` |
| `remove_controller` | Remove a Virtual Controller | `controller*` |
| `set_controller_type` | Change a Controller's Type | `controller*`, `type*`, `preset` |
| `assign_device` | Assign a Device | `device*`, `controller*`, `assigned`, `revision*` |
| `set_mappings` | Set Mapping Rows | `controller*`, `rows`, `clear_all` |
| `auto_map` | Map Automatically | `controller*` |
| `save_macro` | Save a Macro | `controller*`, `index`, `macro*` |
| `delete_macro` | Delete a Macro | `controller*`, `index*` |
| `undo` | Undo Your Changes |  |
| `check_request` | Check a Request | `request_id*` |

## Only the user

No tool turns Automation on or off, changes its level, allows a program, approves a plugin or reads another program's Activity. A place whose reach is `yours-alone` is refused by `set` and `press` and its value is not read.

# PadForge's tools by level

Each level allows what the ones above it allow.

## Commands

| Tool | What it does |
|---|---|
| `status` | The engine, the profile, the window, the controllers, the level |
| `switch_profile` | Puts a profile in force, or with none returns to the default |
| `set_engine` | Starts or stops the engine |
| `set_controllers_power` | Turns every controller, or one, on or off |
| `save_snapshot` | Saves a diagnostics snapshot beside PadForge.exe |

## Read

| Tool | What it does |
|---|---|
| `get_system` | Polling rate, drivers, HidHide |
| `list_profiles`, `get_profile` | The profiles, and what one holds |
| `list_controllers` | The virtual controllers, with outputs and presets on request |
| `list_devices`, `get_device` | The devices, and one device's controls and state |
| `wait_for_input` | The next button, hat or axis the user moves |
| `get_mappings` | Every row of a controller |
| `list_macros` | A controller's macros and menus |
| `record_problem` | A recording for a bug report, with what the recorder found |
| `find_settings`, `get_setting` | Any setting on any page |
| `show` | Opens the page on a setting, device, controller or profile |

## Send Inputs

| Tool | What it does |
|---|---|
| `add_device` | A device of your own, assigned and mapped like any other |
| `set_device_inputs` | Drives your device |
| `remove_device` | Takes your device offline |
| `hold_control` | Holds a control of any device, or lets it go |

## Change Settings

| Tool | What it does |
|---|---|
| `set_setting` | One setting by ID |
| `create_profile`, `rename_profile`, `delete_profile` | Profiles |
| `import_profile`, `export_profile` | Profile files (import always asks) |
| `add_controller`, `remove_controller`, `set_controller_type` | Virtual controllers |
| `assign_device` | Puts a device on a controller or takes it off |
| `set_mappings`, `clear_mappings`, `auto_map` | Mapping rows |
| `save_macro`, `delete_macro` | Macros |
| `undo` | Your latest run of changes, as one step |
| `check_request` | How a change that waited for the user ended |

## Only the user

No tool turns Automation on or off, changes its level, approves a plugin or reads Activity. `set_setting` refuses the settings of the Automation and Plugins pages.

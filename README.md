# DoriDeck for Touch Portal

An unofficial **Touch Portal** plugin that provides an interface for running Dorico commands and scripts directly from Touch Portal.

This plugin is intended to make common Dorico workflows faster by allowing users to trigger recorded macros, execute Dorico API commands, and connect Touch Portal buttons to Dorico-related actions.

## Project Note

I am not a power Dorico user. I use Dorico casually from time to time, mainly for small projects, especially choir-related work. This extension was created to make my own workflow faster and more comfortable, and it currently works very well for my needs.


## Compatibility

> This plugin has been tested with Dorico Pro 5.1.81 on Windows 11 with Touch Portal 4.3
> and Dorico Pro 6.2.31 on  MacOS 15.6 with Touch Portal 4.3

## Installation

1. Download `DoriDeck-win-x64.tpp` or `DoriDeck-osx-arm64.tpp`.
2. In Touch Portal, open Settings → Plug-ins → Import plug-in and select the file.
3. Restart Touch Portal if it asks you to.

## Usage

After installing the plugin, open Macro Deck and add one of the available Dorico actions to a button.

### Available Actions

| Action          | Description                                                                                  | 
| --------------- | -------------------------------------------------------------------------------------------- | 
| **Run Script**  | Executes a recorded Dorico macro.                                                            | 
| **Run Command** | Executes a Dorico command through the API. Examples of available commands can be found [here](https://github.com/Sarti2004/DoriDeck/wiki/DoriDeck-Command-Examples). | 
| **Run predefined Dorico command** |  Picks a common command (dynamics, techniques, articulations, note values) from a list | 
| **Insert Lyrics**    | Syllabify and Paste Lyrics to Dorico. <br>Use custom syllabification dictionaries (`_Words_EN.txt` and so on) here: `%AppData%\DoriDeck\dictionaries` |
| **Pickup Measure**   | Simplify creating a pickup measure                                                      |
| **Rehersal Mark**    | Custom rehearsal mark (Section header). <br>Requires `Finale Maestro Text` font (change it in Library->Paragraph Styles->Rehearsal Marks->Font) |
| **Respell Note**     | Enharmonically Respell note                                                             | 
| **Choir Reduction**  | Create a piano version of a choir score using cues                                      | 
| **Connect**          | Creates a connection between Macro Deck and Dorico.                                     |
| **Clear Connection Token**  | Re-authenticates the connection to Dorico. Use this if MD cannot connect to Dorico.  |
| **Open Recent File**  | Opens the first project in Dorico's recent file list.  |
| **Change Paragraph Style**  |  Change the style of selected staff-attached or system text with a single click.  |
| **Open Previous File**  | Opens the second project in Dorico's recent file list.  |

## Limitations
* Dorico must be running to execute actions successfully.
* Some actions may depend on your Dorico version.
* DoriDeck uses unofficial Dorico API that can change, break, or be disabled at any moment.

## Disclaimer

This project is an independent third-party plugin and is not affiliated with, endorsed by, sponsored by, or otherwise associated with Steinberg Media Technologies GmbH.

Dorico is a trademark or registered trademark of Steinberg Media Technologies GmbH in the United States, Europe, and other countries. All product names, trademarks, and registered trademarks are the property of their respective owners.

Use this plugin at your own risk. The authors and contributors are not responsible for any loss of data, interruption of workflow, software malfunction, or other damages resulting from the use of this plugin. Users are encouraged to back up their Dorico projects before using automation tools or running custom commands.


## Third-Party Licenses

This plugin uses awesome 3rd party libraries:

* [Dorico.Net](https://github.com/scott-janssens/Dorico.Net) — MIT License
* [Moby-Project: mhyph.txt](https://github.com/elitejake/Moby-Project) - Public Domain. See [Details](mhype_notice.md)
* [Bravura](https://github.com/steinbergmedia/bravura) - [SIL Open Font License, Version 1.1](https://openfontlicense.org/).  This pack contains images made with the font, not the font itself, and is not a modified version of Bravura.

## Contributing

Contributions, bug reports, and feature requests are welcome.

When reporting an issue, please include:

* Touch Portal version
* Dorico version
* OS version
* Steps to reproduce the issue
* Expected behavior
* Actual behavior

# Custom models and non-standard installations

Everything here is about telling the tool which code is **yours**. It matters more than it sounds:
the answer decides what gets indexed, what the write path is willing to touch, and what a lint
rule is allowed to complain about.

---

## Declaring your models

```sh
$env:D365FO_CUSTOM_MODELS = "AslCore,AslFleet,ISV_*,!ISV_Sample"
```

The list is matched by `ModelMatcher`:

| Form | Matches |
|---|---|
| `AslCore` | that model, case-insensitively |
| `Asl*`, `ISV_?` | glob — `*` any run of characters, `?` a single one |
| `!ISV_Sample` | negation: excludes what an earlier pattern included |

Patterns are evaluated in order and **the last match wins**, so put the broad pattern first and
the exceptions after it. An empty list matches nothing, which is the safe default: with no custom
models declared, nothing is treated as yours.

The flag is computed at extract time and stored on the model row, so it survives into every later
query. `d365fo models list` shows what the index believes — name, publisher, layer, and whether it is
yours:

```sh
d365fo models list --output json
```

### What the answer changes

| | Custom model | Standard model |
|---|---|---|
| `modify` / the bridge write path | Allowed | Refused unless forced — over-layering Microsoft code is not something to do by accident |
| `lint` and the table rules | Reported | Not reported by default: they are advice for code you own |
| `generate --install-to` | The natural target | Possible, and almost always wrong |
| `index extract` | Same | Same — reading is never restricted |

---

## Two package trees (UDE and split setups)

A Unified Developer Experience installation, and some ISV layouts, keep custom models in a
different folder from the platform's:

```sh
$env:D365FO_PACKAGES_PATH        = "K:\AosService\PackagesLocalDirectory"
$env:D365FO_CUSTOM_PACKAGES_PATH = "C:\Users\me\source\Metadata"
```

`D365FO_CUSTOM_PACKAGES_PATH` takes several roots, semicolon- or comma-separated. They are
indexed for reads **and** forwarded to the bridge, so `generate --install-to <Model>` can resolve
a model that lives outside the primary tree — which is the whole difficulty of the split layout.

> The variable was called `D365FO_EXTRA_PACKAGES_PATH` once. The old name still works as a
> fallback when the new one is unset, `d365fo doctor` warns when it is in use, and it will not be
> honoured forever. Without the fallback, a rename would have silently dropped custom-model roots
> out of the index — a change that looks like nothing and answers like everything is fine.

Per-command, the same thing is `--extra-packages`, repeatable:

```sh
d365fo index extract --packages K:\AosService\PackagesLocalDirectory `
                     --extra-packages C:\Users\me\source\Metadata
```

---

## When the installation is somewhere unusual

```sh
d365fo doctor            # what it found, what it could not, and in which order to fix them
d365fo index status      # the paths actually in effect
```

`doctor` is the first thing to run when something is off; it checks the paths, the index, the
bridge and the label languages, and names the variable to set for each thing it could not find.

| Symptom | Usually |
|---|---|
| `PACKAGES_PATH not found` | The variable is set in a shell that is not the one running the command — Visual Studio's Developer PowerShell in particular ([SETUP.md](SETUP.md#visual-studio-developer-powershell-pitfall)) |
| Custom models missing from `d365fo models` | Their root is not in `D365FO_CUSTOM_PACKAGES_PATH`, or `index extract` has not been re-run since |
| `generate --install-to` cannot find your model | The bridge is not enabled (`D365FO_BRIDGE_ENABLED=1`) or the model's root was not forwarded |
| Labels resolve to `@Fleet:Vehicle` instead of text | `D365FO_LABEL_LANGUAGES` does not include the language, or `--resolve-labels` was not passed |

---

## Keeping the index honest after your own edits

The index is a mirror, and Visual Studio does not tell it when you save:

```sh
d365fo index refresh --model AslFleet             # re-read one model
d365fo index sync ./AxClass/FmVehicleService.xml  # re-read the model that file belongs to
d365fo daemon start                               # or watch the tree and refresh on change (3 s debounce)
```

`index sync` takes the artefact you just edited and re-reads the model around it, which is the
shape an edit actually has — you know the file, not the model name. It exists because an index
built before an external edit answers with full confidence about a file that no longer says that,
and everything that reads `SourcePath` — `get method`, `find refs`, `analyze` — is reading the
file the index remembers.

---

## See also

- [CONFIGURATION.md](CONFIGURATION.md) — every environment variable and the JSON config file.
- [SETUP.md](SETUP.md) — the full install path, including the two-tree case.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — symptoms in more depth.

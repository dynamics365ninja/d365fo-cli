# Seeds

Artefacts a case starts **from** rather than produces.

`generate table-relation` and `generate find-methods` augment a table that already exists: they
merge into its XML and refuse `--out`/`--install-to` outright, because accepting a scaffold-output
option and quietly dropping it would be worse than refusing it. A case for either therefore needs
an input artefact, which is what a seed is. `eval run` copies the seed named by the case's
`apply_to_seed` into the replay's work directory as `actual.xml` and passes `--apply-to` at it, so
the golden is the merged table and nothing here is ever mutated in place.

A seed whose name matches an `AxTable` in `tests/Samples/MiniAot` must stay byte-identical to it —
the index the replay builds comes from the fixture, and a seed that had drifted from it would be a
case merging into a table the tool never saw. `EvalSeedTests` asserts exactly that.

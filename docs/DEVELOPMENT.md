## Formatting

Run `.\format.ps1` to apply repository code style and CRLF line endings across tracked text files.
Run `.\format.ps1 -Check` to verify formatting and CRLF line endings without intentionally changing files.

Line endings are enforced through `.gitattributes` for Git checkouts and `.editorconfig` for Visual Studio/editor behavior.

License files are generated from `CopyrightYear` and `CopyrightHolder` in `Variables.props`:

```sh
.\GenerateLicenses.ps1
```

To enable automatic enforcement, configure Git to use the bundled pre-commit hook:

```sh
git config core.hooksPath .githooks
```

The hook runs the formatter before each commit and stages any resulting changes.

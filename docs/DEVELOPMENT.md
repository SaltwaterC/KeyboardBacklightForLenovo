## Formatting

Run `.\format.ps1` to apply repository code style and CRLF line endings across C#, XML, and PowerShell files.

To enable automatic enforcement, configure Git to use the bundled pre-commit hook:

```sh
git config core.hooksPath .githooks
```

The hook runs the formatter before each commit and stages any resulting changes.

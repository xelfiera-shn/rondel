# Security

Please do not report a security problem in a public issue.

Report it privately on GitHub instead: **[Report a vulnerability](https://github.com/xelfiera-shn/rondel/security/advisories/new)**, also under the repository's **Security** tab. Say what is affected and how to reproduce it, and which Rondel version and platform you use. The report stays between you and the maintainers until a fix is out.

Only the latest release gets fixes. Rondel keeps itself up to date, so a fix reaches everyone who keeps the update check on.

The places where a problem matters most:

- **Updates:** the update check and self-update. A downloaded release is taken only when its checksums carry the signature of Rondel's release key.
- **The local event server:** it runs on 127.0.0.1 and takes the events of the sessions Rondel starts.
- **Deleting:** what Rondel deletes (the Sessions window, and the cleanup after a Quick Conversation).
- **Starting commands:** how Rondel starts commands in a terminal.

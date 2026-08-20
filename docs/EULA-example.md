# End User License Agreement (rough example, not legal advice)

> This is a starting-point draft, not a finished legal document. It was
> generated to give a template to react to, not to be shipped as-is. Have an
> actual lawyer review it before attaching it to any installer or download,
> especially the liability, warranty, and third-party-services sections.
>
> Context for whoever reviews this: the project's *source code* is MIT
> licensed (see [`LICENSE`](../LICENSE)) - MIT already permits use,
> modification, and redistribution, so an EULA is not required for people who
> build the app from source. This draft is for a *compiled binary/installer*
> distributed directly (e.g. the `TokenOptimizer.msi` release asset), where a
> short EULA can set expectations (no warranty, what the app talks to over
> the network) beyond what MIT itself covers. If you don't need that
> distinction, you likely don't need this file at all - MIT alone is enough
> for most personal/hobby tools.

---

## TokenOptimizer End User License Agreement

**Last updated:** [DATE]

This End User License Agreement ("Agreement") is between you ("User") and
[YOUR NAME / ENTITY] ("Licensor") and governs your use of the TokenOptimizer
desktop application and installer ("Software").

### 1. License Grant

The Software's source code is licensed under the MIT License (see `LICENSE`
in the project repository). This Agreement additionally covers the compiled
binary/installer distributed via GitHub Releases: Licensor grants User a
non-exclusive, non-transferable, royalty-free license to install and run the
Software for personal or internal business use.

### 2. Third-Party Services and Network Use

The Software can launch and communicate with third-party services and tools
depending on what the User configures, including but not limited to:
Anthropic's Claude Code CLI, Google Antigravity, Groq's API, OpenCode's Go
API gateway, and locally-run models via the Unsloth CLI. Use of each such
service is subject to that service's own terms and privacy policy. The
Software stores API credentials for these services locally, encrypted with
Windows DPAPI, and does not transmit them anywhere except directly to the
service they authenticate.

### 3. No Warranty

THE SOFTWARE IS PROVIDED "AS IS," WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE, AND NONINFRINGEMENT.

### 4. Limitation of Liability

IN NO EVENT SHALL LICENSOR BE LIABLE FOR ANY CLAIM, DAMAGES, OR OTHER
LIABILITY ARISING FROM, OUT OF, OR IN CONNECTION WITH THE SOFTWARE OR ITS
USE, INCLUDING BUT NOT LIMITED TO COSTS INCURRED FROM THIRD-PARTY API USAGE
(e.g. Groq, OpenCode, Anthropic) THAT THE USER CONFIGURES AND CONTROLS.

### 5. Data Collection

[FILL IN: does the Software collect telemetry, crash reports, or analytics?
If not, say so explicitly - "The Software does not collect or transmit
telemetry, usage analytics, or crash reports." If it does, disclose what,
and whether it's opt-in or opt-out.]

### 6. Termination

This Agreement terminates automatically if User fails to comply with its
terms. Upon termination, User must cease using and delete the Software.

### 7. Governing Law

[FILL IN: your jurisdiction, e.g. "This Agreement is governed by the laws of
the State of [STATE], without regard to conflict-of-law principles."]

### 8. Changes to This Agreement

Licensor may update this Agreement for future releases. Continued use of a
new version after an update constitutes acceptance of the revised terms.

---

**Contact:** [YOUR CONTACT EMAIL OR GITHUB PROFILE]

# Security Policy

## Supported Versions

`wip` is an open-source command-line tool. Security fixes are applied to the latest release and the current `main` branch.

| Version | Supported          |
| ------- | ------------------ |
| 2.9.x   | :white_check_mark: |
| < 2.9.0 | :x:                |

Users are encouraged to stay up to date with the latest release (e.g. via Scoop or GitHub Releases).

## Reporting a Vulnerability

We take the security of `wip` seriously. If you discover a vulnerability, please report it privately. **Do not disclose unpatched vulnerabilities in public GitHub issues, discussions, or pull requests.**

### Preferred Reporting Method: GitHub Private Vulnerability Reporting

The preferred method for reporting security vulnerabilities is GitHub's [Private Vulnerability Reporting](https://github.com/slidict/wip/security/advisories/new).

1. Navigate to the [Advisories page](https://github.com/slidict/wip/security/advisories).
2. Click **"Report a vulnerability"** to submit a private report directly to the maintainers.
3. Collaborate privately with the maintainers in the security advisory to reproduce, patch, and coordinate disclosure.

### What to Include in a Report

To help us understand and resolve the issue quickly, please provide as much relevant information as possible:

- **Type of vulnerability** (e.g., command injection, privilege escalation, path traversal, untrusted argument handling).
- **Affected component / command** (e.g., `wip up`, `wip run`, config parser, sandbox lifecycle).
- **Exact version or commit** of `wip` where the issue was observed.
- **Host environment details** (Windows version, WSL/WSLC versions).
- **Step-by-step reproduction instructions**, including minimal sample configuration files (`wip.yml`, `compose.yml`) or commands.
- **Proof-of-Concept (PoC)** code or script, if available.
- **Impact assessment**: How a potential attacker could exploit this vulnerability and the scope of impact.

### Security Reports vs. Ordinary Bug Reports

- **Security reports** involve issues that compromise confidentiality, integrity, availability, or sandbox isolation (such as executing arbitrary commands outside intended boundaries or leaking sensitive credentials/data). These must be reported through the private vulnerability reporting mechanism above.
- **Ordinary bugs** (functional defects, crashes with benign error messages, documentation typos, or CLI usability issues) should be reported publicly using regular [GitHub Issues](https://github.com/slidict/wip/issues).

## Handling and Coordination Policy

When a private security report is received:

1. **Acknowledgment**: Maintainers will acknowledge receipt of the report after initial verification.
2. **Assessment & Confirmation**: Maintainers will investigate and determine whether the issue is reproducible and in-scope for `wip`.
3. **Fix & Verification**: A fix will be developed in a private branch or advisory fork and tested against the standard test suite.
4. **Coordinated Disclosure**: Once a patched release is published, the advisory will be made public and credit will be given to the reporter (if desired).

We aim to handle all security reports constructively and responsibly. Because `wip` is an open-source project maintained by community contributors, we do not make guaranteed service-level agreements (SLAs) or fixed response-time commitments, but all legitimate vulnerability reports are prioritized for review and remediation.

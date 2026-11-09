# FlaUI.Mcp GitHub Pages Website — Architecture & Design Specification

> **Document Version:** 1.0  
> **Status:** Approved (Brainstorming Complete)  
> **Target URL:** `https://ckir.github.io/flauimcp/`  
> **Inspiration & Styling Baseline:** [ckir.github.io/clavity](https://ckir.github.io/clavity/)

---

## 1. Executive Summary & Purpose

FlaUI.Mcp requires an impressive, authoritative public presence on GitHub Pages. The site demonstrates how AI agents (Claude Code, Antigravity, Cursor, etc.) can safely perceive and control the Windows desktop via native UI Automation 3 (UIA3) under strict operator supervision.

The design mirrors the aesthetic, performance, and structure of `ckir.github.io/clavity`: a dark cyber-neon theme, a cinematic hero section, connected pipeline stages, an interactive dual-pane demo window, clear danger model disclosures, copyable CLI command snippets, and direct links to comprehensive project documentation.

---

## 2. Understanding Summary

* **What is being built:** A single-page, high-performance static product showcase for FlaUI.Mcp hosted on GitHub Pages.
* **Why it exists:** To give FlaUI.Mcp an authoritative landing page that visually demonstrates supervised desktop automation, clearly explains its safety guardrails, and accelerates operator adoption.
* **Who it is for:** AI engineers, automation developers, and enterprise operators connecting agent drivers to Windows.
* **Core Components:**
  1. Semantic navigation bar with release download CTA.
  2. Hero banner featuring high-impact artwork and subline.
  3. 5-Stage Supervised Safety Pipeline chips (*Discover &rarr; Redact &rarr; Authorize &rarr; Actuate &rarr; Audit*).
  4. Interactive Dual-Pane Demo (Agent tool stream on left vs. Windows UIA3 desktop UI with neon target bounding boxes and password redaction shield on right).
  5. Danger model warning and 4 core safety value cards.
  6. Quickstart 3-step guide and copyable CLI command snippets.
  7. Documentation hub linking to in-repo documentation in `docs/`.
  8. Accessible, semantic footer with platform specifications.
* **Key constraints:** Pure vanilla HTML5, modern CSS custom properties, and lightweight vanilla JS (zero npm or external build dependencies), placed in a clean `site/` directory and deployed via GitHub Actions.
* **Explicit non-goals:** No static site generator (Astro/Vite); no conversion of existing markdown files in `docs/`; no backend server, database, or cookie-based tracking.

---

## 3. Assumptions

1. Deployment runs via GitHub's standard `actions/deploy-pages` and `actions/upload-pages-artifact` workflow targeting `site/` on push to `master`.
2. Assets (banner, icon, social card) will live in `site/assets/` in optimized WebP, SVG, and JPG formats.
3. Color system aligns with Clavity's dark palette: deep navy background (`#020713`), electric agent orange (`#ff4b18` / `#ffb14a`), desktop cyan (`#00cfff` / `#00eaff`), warning gold (`#ffbd2e`), and danger red (`#ff5f56`).
4. Commands and parameters reflect the real `flaui-mcp.exe` and `flaui-mcp-setup.exe` CLI documented in `docs/operator-manual.md`.

---

## 4. Decision Log

| # | Topic | Decision | Alternatives Considered | Rationale |
|---|---|---|---|---|
| **D1** | **Site Scope** | Product landing & showcase page mirroring Clavity layout | Full documentation portal; minimal download splash page | Best balances high-impact visual credibility, safety demonstration, and clear quickstart onboarding. |
| **D2** | **Tech Stack** | Vanilla HTML5 + modern CSS + vanilla JS | Astro / Vite / 11ty; Tailwind CLI | Matches Clavity exactly; zero build friction, zero npm CVEs, instant GitHub Pages deployment. |
| **D3** | **Directory Location** | Dedicated `site/` folder with GitHub Action | `docs/` root; orphan `gh-pages` branch | Keeps existing repo documentation in `docs/` clean and untouched; uses modern `actions/deploy-pages`. |
| **D4** | **Interactive Demo** | Option 1: Dual-Pane Agent & Windows Desktop Pairing | Option 2 (Single terminal); Option 3 (Tabbed playground) | Visually and intuitively demonstrates both sides of FlaUI.Mcp: agent tool execution + real Windows UI automation with safety redaction. |
| **D5** | **Hero Artwork** | High-fidelity banner artwork (cyber-neon bridge) + CSS glow | Pure SVG vector only; Canvas WebGL | Delivers the most impressive, cinematic visual impact matching the Clavity hero. |
| **D6** | **Pipeline Chips** | 5-stage Supervised Safety Pipeline (*Discover &rarr; Redact &rarr; Authorize &rarr; Actuate &rarr; Audit*) | Role Architecture; Client Ecosystem chips | Clearly communicates FlaUI.Mcp's core value: safety and supervision at every step. |
| **D7** | **Architecture Model** | Approach 1: Single-Page Narrative Showcase | Multi-page static site; native web components | Lowest complexity, zero maintenance overhead, maximum loading performance. |

---

## 5. Component & Architecture Specifications

### 5.1 Repository Layout
```text
flauimcp/
├── site/
│   ├── index.html          # Semantic HTML5 single-page narrative
│   ├── style.css           # Custom CSS variables, dark cyber-neon theme, responsive layout
│   ├── script.js           # Interactive demo runner, copy buttons, keyboard shortcuts
│   └── assets/
│       ├── banner.webp     # Hero artwork (AI-generated cyber-neon agent-to-desktop bridge)
│       ├── og.jpg          # Social share card (1200x630)
│       ├── flauimcp.svg    # Vector brand icon (Windows window with cyber shield & cursor)
│       └── favicon.ico     # Multi-resolution browser icon
└── .github/
    └── workflows/
        └── deploy-pages.yml # Automated GitHub Actions deployment workflow
```

### 5.2 GitHub Actions Deployment Workflow (`deploy-pages.yml`)
* **Triggers:** Push to `master` with paths `site/**`, plus manual `workflow_dispatch`.
* **Permissions:** `pages: write`, `id-token: write`, `contents: read`.
* **Jobs:**
  * Build: `actions/upload-pages-artifact@v3` with path set to `site/`.
  * Deploy: `actions/deploy-pages@v4`.

### 5.3 Navigation & Hero
* **Navigation (`<nav class="site-nav">`):**
  * Vector brand logo `flauimcp.svg` + label **FlaUI.Mcp**.
  * Smooth-scrolling links: `#demo`, `#safety`, `#how-it-works`, `#install`, plus external link to GitHub documentation.
  * Nav CTA: "Download Setup" pointing directly to the latest `flaui-mcp-setup.exe`.
* **Hero Banner (`<header class="hero">`):**
  * Responsive banner image (`site/assets/banner.webp`) with glowing border and subtle radial gradient.
  * Headline: *"Supervised Windows Automation for AI Agents"*.
  * Subline: *"Model Context Protocol server translating agent intent into native UI Automation 3 under strict operator safety guardrails."*.
  * Action Buttons: Primary gradient CTA (*Get Started*) & Secondary outline CTA (*Read Safety Model*).
* **5-Stage Pipeline Chips (`<ol class="stages">`):**
  1. **Discover:** `desktop_find_element` &bull; Native UIA3 tree search & bounding rectangles.
  2. **Redact:** Zero-leak privacy shield &bull; Passwords & secrets censored across text and pixels.
  3. **Authorize:** Zero-trust lease gate &bull; Synthetic clicks/typing locked by default until granted.
  4. **Actuate:** Pattern actuation &bull; Pattern-based invoke or supervised OS-level synthetic input.
  5. **Audit:** Tamper-evident trail &bull; Every command, bounding rect, and lease logged.

### 5.4 Dual-Pane Interactive Demo Window (`<section id="demo">`)
* **Window Frame:** Dark navy (`#000c1f`) container with macOS/Win-style status dots and title `FlaUI.Mcp — Supervised Session`.
* **Left Pane (Agent Driver Stream):**
  * Shows realistic MCP JSON-RPC tool calls (`desktop_find_element`, `desktop_read_text`, `desktop_click`).
  * Guardrail checks showing active synthetic input lease timers and bounding coordinate checks.
  * Controls: `[Next Step]`, `[Auto-Play]`, and `[Reset]`.
* **Right Pane (Simulated Windows Target):**
  * Mock Windows application (`Enterprise Admin Console`).
  * Target element highlighting: neon cyan bounding rectangle (`#00cfff`) with `TARGETED` label.
  * Password Redaction: censored input box with diagonal red warning stripes, `<REDACTED: IsPassword=True>` text, and red shield badge.
  * Animated visual cursor indicating synthetic actuation point.

### 5.5 Safety Pillars & Values Grid (`<section id="safety">`)
* Prominent Warning Box replicating FlaUI.Mcp's core danger model (no sandbox, real OS input, synthetic input locked by default).
* 4 Core Value Cards:
  1. *Zero-Leak Redaction* (Password fields censored across tree, text, diffs, and screenshots).
  2. *Zero-Trust Input Leases* (Read-only by default; timed leases required for actuation).
  3. *Supervised Non-Determinism* (Full session audit logs of all commands and coordinates).
  4. *Native UIA3 Precision* (Direct accessibility tree targeting instead of guessing pixels).

### 5.6 Quickstart & Installation (`<section id="install">`)
* 3-Step Setup Guide:
  1. Download `flaui-mcp-setup.exe`.
  2. Run installer (SmartScreen approval & automated CLI registration).
  3. CLI Commands with one-click **[Copy]** buttons and visual feedback:
     * `flaui-mcp-setup.exe --silent`
     * `claude mcp add flauimcp -- flaui-mcp.exe`
     * `flaui-mcp lease grant --duration 10m`

### 5.7 Resilience, Accessibility & Performance
* **Zero Cumulative Layout Shift (CLS):** Explicit aspect ratios and image dimensions.
* **No-JS Fallback:** 100% semantic HTML; functions fully even if JavaScript is disabled.
* **Clipboard Fallback:** Automatic fallback to `document.execCommand('copy')` if `navigator.clipboard` is restricted.
* **WCAG 2.2 AA:** Contrast ratios exceeding 4.5:1 (text exceeds 7:1), `:focus-visible` outline indicators, full keyboard operability, and `@media (prefers-reduced-motion: reduce)` support.

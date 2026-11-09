# FlaUI.Mcp GitHub Pages Website Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement a high-performance, dark-themed static showcase website for FlaUI.Mcp hosted on GitHub Pages, matching the visual polish, responsive structure, and design aesthetic of https://ckir.github.io/clavity/.

**Architecture:** Single-page semantic HTML5 layout with zero npm dependencies, styled via modern CSS custom properties and driven by vanilla JavaScript for demo interactivity and one-click CLI copying. Packaged in a clean `site/` folder and deployed automatically to GitHub Pages via an official GitHub Actions workflow.

**Tech Stack:** Vanilla HTML5, Modern CSS (CSS Grid, Flexbox, custom properties), Vanilla ES6 JavaScript, GitHub Actions (`actions/deploy-pages@v4`), WebP / SVG graphic assets.

---

### Task 1: Scaffolding and Visual Brand Assets

**Files:**
- Create: `site/assets/flauimcp.svg`
- Create: `site/assets/favicon.ico`
- Create: `site/assets/og.jpg`
- Create: `site/assets/banner.webp`

**Step 1: Create vector brand icon (`site/assets/flauimcp.svg`)**
Create the cybernetic window and safety shield SVG logo with neon cyan and agent orange gradients.

**Step 2: Generate or convert banner artwork (`site/assets/banner.webp`)**
Generate the 16:9 banner visual depicting the AI terminal connected across a cyber-neon bridge to Windows UI Automation elements, optimized to WebP with Pillow or ImageMagick.

**Step 3: Generate social preview card (`site/assets/og.jpg`) and favicon (`site/assets/favicon.ico`)**
Generate the Open Graph 1200x630 social preview card and browser favicon.

**Step 4: Verify asset presence and integrity**
Run: `Test-Path site/assets/flauimcp.svg, site/assets/banner.webp, site/assets/og.jpg, site/assets/favicon.ico`
Expected: `True, True, True, True`

**Step 5: Commit**
```bash
git add site/assets/
git commit -m "feat(site): add brand assets, vector logo, and hero banner"
```

---

### Task 2: Core Styling and Theme Architecture (`site/style.css`)

**Files:**
- Create: `site/style.css`

**Step 1: Define CSS Custom Properties and Reset**
Define color tokens (`--bg`, `--bg-deep`, `--bg-mid`, `--agent`, `--agent-soft`, `--desktop`, `--desktop-soft`, `--text`, `--muted`, `--panel`), typography, box-sizing, and dark-scheme defaults.

**Step 2: Implement Component Styles**
Implement styles for:
- Navigation bar (`.site-nav`, `.nav-wrap`, `.nav-brand`, `.nav-cta`)
- Hero banner and glowing accents (`.hero`, `.banner`, `.subline`, `.hero-actions`)
- 5-stage pipeline chips (`.stages`, `.stages li`, `.stages .agent`, `.stages .desktop`, arrow pseudo-elements)
- Dual-pane demo window (`.demo-window`, `.demo-header`, `.demo-pane`, `.mock-win`, `.redacted-field`, `.targeted-elem`)
- Safety pillars and value cards (`.warning-box`, `.cards`, `.card`, `.badge`)
- Quickstart CLI commands (`.commands`, `.commands li`, `.copy-btn`)
- Footer and responsive breakpoints (mobile 320px, tablet 768px, desktop 1200px+)
- Accessibility focus outlines and `@media (prefers-reduced-motion: reduce)`

**Step 3: Verify CSS syntax and rules**
Run a syntax check and inspect with headless browser / node linter.

**Step 4: Commit**
```bash
git add site/style.css
git commit -m "feat(site): add modern responsive CSS theme matching Clavity aesthetic"
```

---

### Task 3: Semantic HTML5 Structure (`site/index.html`)

**Files:**
- Create: `site/index.html`

**Step 1: Write Document Head & Meta Tags**
Include UTF-8 encoding, viewport, SEO meta description, Open Graph tags, canonical link, and stylesheet references.

**Step 2: Construct Navigation, Hero, and Pipeline Stages**
Assemble `<nav>`, `<header class="hero">`, and `<ol class="stages">` with the 5 stages: Discover, Redact, Authorize, Actuate, Audit.

**Step 3: Construct Dual-Pane Demo Section (`#demo`)**
Assemble the dual-pane demo window with Left (Agent Driver JSON-RPC log + step controls) and Right (simulated Windows app with neon bounding box, simulated cursor, and redacted password field).

**Step 4: Construct Safety Callout, Value Cards, Quickstart, and Footer**
Assemble `<section id="safety">` (danger notice + 4 cards), `<section id="install">` (installer steps + copyable CLI commands), `<section id="docs">` (documentation links to in-repo markdown), and `<footer>`.

**Step 5: Verify HTML structure**
Verify that all links, IDs, and image sources match the files created in Task 1.

**Step 6: Commit**
```bash
git add site/index.html
git commit -m "feat(site): construct semantic HTML5 landing page structure"
```

---

### Task 4: Interactive Script Engine (`site/script.js`)

**Files:**
- Create: `site/script.js`

**Step 1: Implement Demo State Machine**
Build a 4-step interactive scenario runner:
- Step 1: Agent calls `desktop_find_element` &rarr; Highlights target window in right pane.
- Step 2: Agent reads application tree &rarr; Highlights redacted password field.
- Step 3: Agent checks synthetic lease &rarr; Displays lease countdown badge in header.
- Step 4: Agent calls `desktop_click` &rarr; Target button pulses neon glow, synthetic cursor actuates.
Support manual clicking `[Next Step]`, `[Auto-Play]`, and `[Reset]`.

**Step 2: Implement One-Click Clipboard Utility**
Implement clipboard copy logic for all CLI command buttons with automatic fallback to `execCommand('copy')` if `navigator.clipboard` is unavailable, providing visual *"Copied!"* feedback.

**Step 3: Verify Script Functions**
Run local automated checks in Node or browser console to ensure zero runtime exceptions.

**Step 4: Commit**
```bash
git add site/script.js
git commit -m "feat(site): add interactive demo runner and clipboard copy utility"
```

---

### Task 5: GitHub Pages CI/CD Deployment Workflow (`deploy-pages.yml`)

**Files:**
- Create: `.github/workflows/deploy-pages.yml`

**Step 1: Write Deployment Workflow**
Configure `.github/workflows/deploy-pages.yml` using `actions/upload-pages-artifact@v3` and `actions/deploy-pages@v4` on push to `master` with paths `site/**`.

**Step 2: Validate YAML syntax**
Run: `pwsh -Command "Get-Content .github/workflows/deploy-pages.yml | Out-Null"`
Expected: No parsing errors.

**Step 3: Commit**
```bash
git add .github/workflows/deploy-pages.yml
git commit -m "ci: add GitHub Actions workflow to auto-deploy site/ to GitHub Pages"
```

---

### Task 6: End-to-End Verification and Local Browser Review

**Files:**
- Test: Local HTTP server serving `site/`

**Step 1: Serve site locally**
Run: `python -m http.server 8080 --directory site`

**Step 2: Verify responsive rendering and interactions**
- Check mobile layout at 375px width.
- Check desktop layout at 1280px+ width.
- Test interactive demo buttons (`[Next Step]`, `[Auto-Play]`, `[Reset]`).
- Test copy command buttons.
- Confirm zero browser console errors.

**Step 3: Shut down local server and finalize**
Terminate local server process.

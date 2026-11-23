# Mills814 — storefront redesign

A minimal, futuristic redesign of [mills814store.com](https://mills814store.com).
It is a static site (HTML, CSS and plain JavaScript, no build step) that uses the store's real
catalog and checks out through the existing Shopify store.

## Run it

```bash
cd mills814-redesign
python3 -m http.server 8814
# open http://localhost:8814
```

Opening `index.html` straight from disk also works.

## What's in it

| Area | Details |
| --- | --- |
| **Brand** | `MILLS 814` wordmark with a rounded-square **M** mark (`assets/mark.svg`). One accent color, **Volt `#d7ff3f`**, on near-black. Fonts: Space Grotesk for display and body, JetBrains Mono for labels and prices. All tokens are at the top of `assets/styles.css`. |
| **Hero** | Animated perspective grid, a glow that follows the cursor, headline lines that slide up into view, and "everyday." filling in with Volt. |
| **Motion** | Content fades in as you scroll, buttons lean toward the cursor, cards light up under the cursor, product images swap on hover, the category ticker scrolls, the manifesto lights up word by word as you scroll, a scroll-progress line sits under the header, and added items fly into the cart. All of it turns off when the visitor's system is set to *reduce motion*. |
| **Buttons** | Volt primary button with a glow and a light sweep on hover, outlined ghost button, round icon buttons, a slight press effect on click, and a clear outline for keyboard focus. |
| **Shop** | Division tabs (All / Grooming / Car Care / Outdoor) with a sliding highlight, category chips with counts, instant search (press `/`), sorting, "Load more". |
| **Product pop-up** | Image gallery, option pickers (sold-out combinations are crossed out), quantity, and a link to the full product page. |
| **Cart** | Slide-out cart kept in the visitor's browser. **Checkout** opens a real Shopify cart link (`/cart/<variant>:<qty>,…`), so orders go through the existing store. |
| **Newsletter** | Posts to the store's existing Shopify customer form (tagged `newsletter`). |

## Updating products

`assets/catalog.js` is generated from the store's public `/products.json`:

```bash
python3 tools/build_catalog.py               # pull the live catalog
python3 tools/build_catalog.py saved.json    # or use a saved copy
```

The script shortens long marketplace titles, sorts each product into a division and category
(rules at the top of the script), and serves images through the store's own domain with
`?width=` resizing. To change the featured row or the image on each division card, edit
`FEATURED` / `DIVISIONS` at the top of `assets/app.js`.

## Going live on Shopify

This is a stand-alone front end. To make it the real storefront, either:

1. **Port it into the theme:** move the CSS into the Horizon/Drive theme (or a custom theme) and
   rebuild the sections in Liquid, replacing `catalog.js` with Liquid's `product` and
   `collection` objects and the cart with Shopify's `/cart/add.js` endpoint; or
2. **Host it as a headless storefront** (for example on Vercel or Netlify) and keep checkout on Shopify
   as it works now.

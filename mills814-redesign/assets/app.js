/* Mills814 storefront — catalog, cart, and motion. No dependencies. */
(() => {
  "use strict";

  const STORE = window.STORE_URL;
  const ALL = window.CATALOG || [];
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
  const finePointer = matchMedia("(hover: hover) and (pointer: fine)").matches;
  const money = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
  const fmt = (n) => money.format(n || 0);
  const esc = (s = "") => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const sized = (src, w) => (src ? `${src}${src.includes("?") ? "&" : "?"}width=${w}` : "");
  const attr = (src, w) => esc(sized(src, w));
  const productUrl = (p) => `${STORE}/products/${encodeURIComponent(p.h)}`;

  const DIVISIONS = {
    car: { name: "Car Care", blurb: "Blackline® wash, interior & detailing tools", hero: "blackline®-foam-cannon-pro" },
    grooming: { name: "Grooming", blurb: "Beard care, natural soap, deodorant & skin", hero: "top-seller-family-bundle-8-pack" },
    outdoor: { name: "Outdoor", blurb: "Rooftop tents & solar power", hero: "explorer-plus-rooftop-tent-hardshell-with-side-awning-air-conditioner-outlet-with-bracket-luggage-racks-replaceable-netting-rain-curtain-truck-bed-tent-for-camping-pop-up-suv-roof-top-tent" },
  };
  const FEATURED = [
    "blackline®-ultra-foam",
    "blackline®-foam-cannon-pro",
    "blackline®-twisted-loop-drying-towel",
    "blackline®-reset-interior-cleaner-scent-booster",
    "blackline®-speed-gloss-quick-detailer",
    "blackline®-crystal-glass-cleaner",
    "1776-revolutionary-bay-rum-soap-5-56-oz-natural-bar",
    "angry-charlie-beard-bundle-balm-butter-oil-natural-soap",
    "hand-salve",
  ];

  const byHandle = new Map(ALL.map((p) => [p.h, p]));
  const variantIndex = new Map();
  ALL.forEach((p, i) => {
    p._i = i;
    p._f = FEATURED.includes(p.h) ? FEATURED.indexOf(p.h) : 100 + i;
    p.v.forEach((v) => variantIndex.set(String(v.id), { p, v }));
  });

  /* ── product helpers ─────────────────────────────────────────────── */
  const available = (p) => p.v.some((v) => v.a);
  const cheapest = (p) => p.v.reduce((a, b) => ((b.a && !a.a) || (b.a === a.a && b.p < a.p) ? b : a), p.v[0]);
  const minPrice = (p) => cheapest(p).p;
  const discount = (v) => (v.c && v.c > v.p ? Math.round((1 - v.p / v.c) * 100) : 0);
  const onSale = (p) => p.v.some((v) => v.a && discount(v) > 0);
  const priceHTML = (p, v = null) => {
    const varies = !v && new Set(p.v.map((x) => x.p)).size > 1;
    const show = v || cheapest(p);
    return `<span class="price">${varies ? '<span class="from">from</span>' : ""}<span>${fmt(show.p)}</span>${discount(show) ? `<s>${fmt(show.c)}</s>` : ""}</span>`;
  };

  /* ── product card ────────────────────────────────────────────────── */
  function cardHTML(p, i = 0) {
    const [a, b] = p.img;
    const offs = [...new Set(p.v.filter((v) => v.a).map(discount))];
    const sale = onSale(p) && (offs.length === 1 ? `−${offs[0]}%` : "Sale");
    const soldOut = !available(p);
    const multi = p.v.length > 1;
    return `
      <article class="card" data-h="${esc(p.h)}" style="--i:${i}">
        <div class="card__media${b ? " has-alt" : ""}">
          <div class="tags">
            ${soldOut ? '<span class="tag tag--out">Sold out</span>' : sale ? `<span class="tag tag--sale">${sale}</span>` : ""}
          </div>
          ${a ? `<img class="main" src="${attr(a, 600)}" alt="" loading="lazy" decoding="async" />` : ""}
          ${b ? `<img class="alt" src="${attr(b, 600)}" alt="" loading="lazy" decoding="async" />` : ""}
          ${soldOut ? "" : `<button class="quick" data-quick aria-label="${multi ? "Choose options for" : "Add to cart:"} ${esc(p.n)}">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg><span>${multi ? "Choose options" : "Quick add"}</span>
          </button>`}
        </div>
        <div class="card__body">
          <span class="card__brand">${esc(p.b)} · ${esc(p.c)}</span>
          <h3 class="card__name"><button class="card__open" data-open>${esc(p.n)}</button></h3>
          ${priceHTML(p)}
        </div>
      </article>`;
  }

  /* ── divisions + rail + hero counts ──────────────────────────────── */
  function renderDivisions() {
    const html = Object.entries(DIVISIONS).map(([key, d], i) => {
      const items = ALL.filter((p) => p.d === key);
      const from = Math.min(...items.map(minPrice));
      const hero = byHandle.get(d.hero) || items[0];
      return `
        <a class="div-card" href="#shop" data-go="${key}" data-reveal style="--d:${i * 0.1}s">
          <div class="div-card__top"><span>0${i + 1}</span><span>${items.length} items</span></div>
          <div class="div-card__img"><img src="${attr(hero.img[0], 700)}" alt="" loading="lazy" /></div>
          <div class="div-card__foot">
            <div>
              <h3 class="div-card__name">${d.name}</h3>
              <p class="div-card__meta">${d.blurb} · from ${fmt(from)}</p>
            </div>
            <span class="div-card__go" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M5 12h14M13 6l6 6-6 6"/></svg></span>
          </div>
        </a>`;
    }).join("");
    $("#divisions").innerHTML = html;
  }

  function renderRail() {
    $("#rail").innerHTML = FEATURED.map((h) => byHandle.get(h)).filter(Boolean).map(cardHTML).join("");
  }

  function countUp() {
    $$("[data-count]").forEach((el) => {
      const target = ALL.filter((p) => p.d === el.dataset.count).length;
      const label = (n) => `${String(n).padStart(2, "0")} items`;
      if (reduceMotion) return (el.textContent = label(target));
      const t0 = performance.now();
      const tick = (t) => {
        const k = Math.min(1, (t - t0) / 1400);
        el.textContent = label(Math.round(target * (1 - Math.pow(1 - k, 3))));
        if (k < 1) requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });
  }

  /* ── shop: filter / search / sort ────────────────────────────────── */
  const PAGE = 16;
  const state = { div: "all", cat: null, q: "", sort: "featured", limit: PAGE };

  function filtered() {
    const q = state.q.trim().toLowerCase();
    let list = ALL.filter((p) =>
      (state.div === "all" || p.d === state.div) &&
      (!state.cat || p.c === state.cat) &&
      (!q || `${p.n} ${p.b} ${p.c} ${p.s || ""} ${p.desc}`.toLowerCase().includes(q))
    );
    const sorters = {
      featured: (a, b) => (available(b) - available(a)) || a._f - b._f,
      "price-asc": (a, b) => minPrice(a) - minPrice(b),
      "price-desc": (a, b) => minPrice(b) - minPrice(a),
      sale: (a, b) => (onSale(b) - onSale(a)) || a._f - b._f,
      az: (a, b) => a.n.localeCompare(b.n),
    };
    if (state.sort === "sale") list = list.filter(onSale);
    return list.sort(sorters[state.sort]);
  }

  function renderChips() {
    const pool = ALL.filter((p) => state.div === "all" || p.d === state.div);
    const ORDER = ["Wash", "Interior", "Finish", "Tools", "Beard", "Soap", "Deodorant", "Skin", "Sets", "Tents", "Solar"];
    const cats = [...new Set(pool.map((p) => p.c))].sort((a, b) => ORDER.indexOf(a) - ORDER.indexOf(b));
    const count = (c) => pool.filter((p) => p.c === c).length;
    $("#chips").innerHTML =
      `<button class="chip" style="--i:0" data-cat="" aria-pressed="${!state.cat}">All<sup>${pool.length}</sup></button>` +
      cats.map((c, i) => `<button class="chip" style="--i:${i + 1}" data-cat="${esc(c)}" aria-pressed="${state.cat === c}">${esc(c)}<sup>${count(c)}</sup></button>`).join("");
  }

  function renderGrid() {
    const list = filtered();
    const shown = list.slice(0, state.limit);
    $("#grid").innerHTML = shown.map((p, i) => cardHTML(p, i)).join("");
    $("#resultCount").textContent = String(list.length).padStart(2, "0");
    $("#empty").hidden = list.length > 0;
    $("#shopTitle").firstChild.textContent = (state.div === "all" ? "All products" : DIVISIONS[state.div].name) + " ";
    let more = $("#more");
    if (list.length > state.limit) {
      if (!more) {
        $("#grid").insertAdjacentHTML("afterend", `<div class="more-wrap" id="more"><button class="btn btn--ghost" data-magnetic>Load more <span class="mono"></span></button></div>`);
        more = $("#more");
        more.firstElementChild.addEventListener("click", () => { state.limit += PAGE; renderGrid(); });
        bindMagnetic(more);
      }
      more.querySelector(".mono").textContent = `+${Math.min(PAGE, list.length - state.limit)}`;
    } else if (more) more.remove();
  }

  function moveSegPill() {
    const active = $(`#seg [data-div="${state.div}"]`);
    const pill = $(".seg__pill");
    pill.style.width = `${active.offsetWidth}px`;
    pill.style.transform = `translateX(${active.offsetLeft}px)`;
  }

  function setDivision(div) {
    state.div = div; state.cat = null; state.limit = PAGE;
    $$("#seg [data-div]").forEach((b) => b.setAttribute("aria-selected", String(b.dataset.div === div)));
    moveSegPill(); renderChips(); renderGrid();
  }

  /* ── cart ─────────────────────────────────────────────────────────── */
  const CART_KEY = "mills814.cart";
  let cart = [];
  try { cart = JSON.parse(localStorage.getItem(CART_KEY) || "[]").filter((l) => variantIndex.has(String(l.id))); } catch { cart = []; }
  const saveCart = () => { try { localStorage.setItem(CART_KEY, JSON.stringify(cart)); } catch { /* private mode */ } };

  function addToCart(id, q = 1) {
    const line = cart.find((l) => String(l.id) === String(id));
    line ? (line.q += q) : cart.push({ id, q });
    saveCart(); renderCart(true);
    const { p } = variantIndex.get(String(id));
    toast(`Added · ${p.n}`);
  }

  function renderCart(bump = false) {
    const items = cart.map((l) => ({ ...l, ...variantIndex.get(String(l.id)) }));
    const n = items.reduce((s, l) => s + l.q, 0);
    const total = items.reduce((s, l) => s + l.q * l.v.p, 0);
    const badge = $("#cartCount");
    badge.textContent = n;
    badge.classList.toggle("has-items", n > 0);
    if (bump && !reduceMotion) { badge.classList.remove("bump"); void badge.offsetWidth; badge.classList.add("bump"); }
    $("#drawerCount").textContent = n ? `(${n})` : "";
    $("#cartTotal").textContent = fmt(total);
    const checkout = $("#checkout");
    checkout.setAttribute("aria-disabled", String(!n));
    checkout.href = n ? `${STORE}/cart/${items.map((l) => `${l.id}:${l.q}`).join(",")}` : "#";
    $("#cartItems").innerHTML = n
      ? items.map((l) => `
        <div class="line-item">
          <div class="line-item__img"><img src="${attr(l.v.img || l.p.img[0], 200)}" alt="" /></div>
          <div>
            <p class="line-item__name">${esc(l.p.n)}</p>
            <p class="line-item__var">${l.v.t !== "Default Title" ? esc(l.v.t) : esc(l.p.b)}</p>
            <div class="qty"><button data-line="${l.id}" data-d="-1" aria-label="Decrease">−</button><output>${l.q}</output><button data-line="${l.id}" data-d="1" aria-label="Increase">+</button></div>
          </div>
          <div class="line-item__right"><span>${fmt(l.v.p * l.q)}</span><button class="line-item__rm" data-rm="${l.id}">Remove</button></div>
        </div>`).join("")
      : `<div class="drawer__empty"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16l-1.5 11a2 2 0 0 1-2 1.7h-9a2 2 0 0 1-2-1.7z"/><path d="M9 7V5a3 3 0 0 1 6 0v2"/></svg><p>Your cart is empty.</p><a class="btn btn--ghost" href="#shop" data-close-cart>Start shopping</a></div>`;
  }

  function openCart() {
    const d = $("#drawer"), s = $("#scrim");
    s.hidden = false; requestAnimationFrame(() => { s.classList.add("on"); d.classList.add("on"); });
    d.setAttribute("aria-hidden", "false"); document.body.classList.add("locked");
    $("#cartClose").focus();
  }
  function closeCart() {
    const d = $("#drawer"), s = $("#scrim");
    s.classList.remove("on"); d.classList.remove("on"); d.setAttribute("aria-hidden", "true");
    document.body.classList.remove("locked");
    setTimeout(() => (s.hidden = true), 400);
  }

  function flyToCart(fromEl) {
    if (reduceMotion || !fromEl) return;
    const img = fromEl.querySelector("img.main, img") ;
    if (!img) return;
    const a = img.getBoundingClientRect(), b = $("#cartOpen").getBoundingClientRect();
    const ghost = img.cloneNode();
    Object.assign(ghost.style, { position: "fixed", left: `${a.left}px`, top: `${a.top}px`, width: `${a.width}px`, height: `${a.height}px`, zIndex: 90, objectFit: "contain", borderRadius: "16px", background: "var(--tile)", pointerEvents: "none" });
    document.body.appendChild(ghost);
    const dx = b.left + b.width / 2 - (a.left + a.width / 2), dy = b.top + b.height / 2 - (a.top + a.height / 2);
    ghost.animate(
      [{ transform: "none", opacity: 1 }, { transform: `translate(${dx * 0.6}px, ${dy * 0.3 - 60}px) scale(.45)`, opacity: 1, offset: 0.6 }, { transform: `translate(${dx}px, ${dy}px) scale(.06)`, opacity: 0.2 }],
      { duration: 800, easing: "cubic-bezier(.65,0,.35,1)" }
    ).onfinish = () => ghost.remove();
  }

  function confirmButton(btn, label = "Added") {
    if (!btn) return;
    const html = btn.innerHTML;
    btn.classList.add("is-done");
    btn.innerHTML = `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m5 12 5 5 9-10"/></svg><span>${label}</span>`;
    setTimeout(() => { btn.classList.remove("is-done"); btn.innerHTML = html; }, 1400);
  }

  /* ── quick view ──────────────────────────────────────────────────── */
  const qv = $("#qv");
  let cur = null;

  function openQuickView(p) {
    const start = p.v.find((v) => v.a) || p.v[0];
    cur = { p, sel: [...start.o], qty: 1, imgIdx: 0, v: start };
    $("#qvBrand").textContent = `${p.b} · ${p.c}`;
    $("#qvName").textContent = p.n;
    $("#qvSpec").textContent = p.s || "";
    $("#qvDesc").textContent = p.desc;
    $("#qvMore").href = productUrl(p);
    $("#qvThumbs").innerHTML = p.img.length > 1
      ? p.img.map((src, i) => `<button data-img="${i}" aria-label="Image ${i + 1}" aria-current="${i === 0}"><img src="${attr(src, 160)}" alt="" loading="lazy" /></button>`).join("")
      : "";
    renderQuickView();
    qv.showModal();
    document.body.classList.add("locked");
  }

  function resolveVariant(changed) {
    const { p, sel } = cur;
    return p.v.find((v) => v.o.every((x, i) => x === sel[i])) ||
      p.v.find((v) => v.a && v.o[changed] === sel[changed]) ||
      p.v.find((v) => v.o[changed] === sel[changed]) || p.v[0];
  }

  function renderQuickView(showImg) {
    const { p, sel, v } = cur;
    $("#qvOpts").innerHTML = p.opts.map((name, oi) => {
      const values = [...new Set(p.v.map((x) => x.o[oi]))];
      const label = name.replace(/\s*\(.*\)$/, "");
      return `<div class="opt"><p class="opt__label">${esc(label)}<b>${esc(sel[oi])}</b></p><div class="opt__vals">${values.map((val) => {
        const ok = p.v.some((x) => x.a && x.o[oi] === val && x.o.every((y, j) => j === oi || y === sel[j]));
        return `<button data-opt="${oi}" data-val="${esc(val)}" aria-pressed="${sel[oi] === val}" class="${ok ? "" : "na"}">${esc(val)}</button>`;
      }).join("")}</div></div>`;
    }).join("");
    $("#qvPrice").innerHTML = priceHTML(p, v) + (discount(v) ? ` <span class="tag tag--sale">−${discount(v)}%</span>` : "");
    const add = $("#qvAdd");
    add.disabled = !v.a;
    add.textContent = v.a ? `Add to cart — ${fmt(v.p * cur.qty)}` : "Sold out";
    $("#qvQty").textContent = cur.qty;
    const src = showImg ?? (v.img || p.img[cur.imgIdx] || p.img[0]);
    const im = $("#qvImg");
    if (im.dataset.src !== src) {
      im.dataset.src = src; im.src = sized(src, 1000); im.alt = p.n;
      im.style.animation = "none"; void im.offsetWidth; im.style.animation = "";
    }
    $$("#qvThumbs button").forEach((b) => b.setAttribute("aria-current", String(p.img[+b.dataset.img] === src)));
  }

  function closeQuickView() { qv.close(); }
  qv.addEventListener("close", () => document.body.classList.remove("locked"));
  qv.addEventListener("click", (e) => {
    if (e.target === qv || e.target.closest("[data-close]")) return closeQuickView();
    const opt = e.target.closest("[data-opt]");
    if (opt) {
      const i = +opt.dataset.opt;
      cur.sel[i] = opt.dataset.val;
      cur.v = resolveVariant(i);
      cur.sel = [...cur.v.o];
      return renderQuickView();
    }
    const th = e.target.closest("[data-img]");
    if (th) { cur.imgIdx = +th.dataset.img; return renderQuickView(cur.p.img[cur.imgIdx]); }
    const q = e.target.closest("[data-q]");
    if (q) { cur.qty = Math.max(1, Math.min(99, cur.qty + +q.dataset.q)); return renderQuickView($("#qvImg").dataset.src); }
    if (e.target.closest("#qvAdd") && cur.v.a) {
      addToCart(cur.v.id, cur.qty);
      flyToCart($(".qv__img"));
      setTimeout(closeQuickView, 350);
    }
  });

  /* ── toast ────────────────────────────────────────────────────────── */
  let toastTimer;
  function toast(msg) {
    const t = $("#toast");
    t.textContent = msg; t.classList.add("on");
    clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove("on"), 2200);
  }

  /* ── global events ───────────────────────────────────────────────── */
  document.addEventListener("click", (e) => {
    const go = e.target.closest("[data-go]");
    if (go) setDivision(go.dataset.go);

    const card = e.target.closest(".card");
    if (card) {
      const p = byHandle.get(card.dataset.h);
      const quick = e.target.closest("[data-quick]");
      if (quick && p.v.length === 1) {
        addToCart(p.v[0].id, 1); flyToCart(card.querySelector(".card__media")); confirmButton(quick);
      } else openQuickView(p);
      return;
    }

    const seg = e.target.closest("#seg [data-div]");
    if (seg) return setDivision(seg.dataset.div);
    const chip = e.target.closest("[data-cat]");
    if (chip) {
      state.cat = chip.dataset.cat || null; state.limit = PAGE;
      $$("#chips .chip").forEach((c) => c.setAttribute("aria-pressed", String((c.dataset.cat || null) === state.cat)));
      return renderGrid();
    }
    const rail = e.target.closest("[data-rail]");
    if (rail) { const r = $("#rail"); r.scrollBy({ left: +rail.dataset.rail * r.clientWidth * 0.8, behavior: reduceMotion ? "auto" : "smooth" }); }

    const line = e.target.closest("[data-line]");
    if (line) {
      const l = cart.find((x) => String(x.id) === line.dataset.line);
      l.q += +line.dataset.d; if (l.q < 1) cart = cart.filter((x) => x !== l);
      saveCart(); renderCart();
    }
    const rm = e.target.closest("[data-rm]");
    if (rm) { cart = cart.filter((x) => String(x.id) !== rm.dataset.rm); saveCart(); renderCart(); }
    if (e.target.closest("[data-close-cart]")) closeCart();
  });

  $("#cartOpen").addEventListener("click", openCart);
  $("#cartClose").addEventListener("click", closeCart);
  $("#scrim").addEventListener("click", closeCart);
  $("#checkout").addEventListener("click", (e) => { if (!cart.length) e.preventDefault(); });
  $("#searchOpen").addEventListener("click", () => {
    $("#shop").scrollIntoView({ behavior: reduceMotion ? "auto" : "smooth" });
    setTimeout(() => $("#search").focus({ preventScroll: true }), reduceMotion ? 0 : 600);
  });

  let searchTimer;
  $("#search").addEventListener("input", (e) => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => { state.q = e.target.value; state.limit = PAGE; renderGrid(); }, 120);
  });
  $("#sort").addEventListener("change", (e) => { state.sort = e.target.value; state.limit = PAGE; renderGrid(); });

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && $("#drawer").classList.contains("on")) closeCart();
    if (e.key === "/" && !/input|textarea|select/i.test(document.activeElement.tagName) && !qv.open) {
      e.preventDefault(); $("#searchOpen").click();
    }
  });

  /* ── motion ──────────────────────────────────────────────────────── */
  const io = new IntersectionObserver((entries) => {
    entries.forEach((en) => { if (en.isIntersecting) { en.target.classList.add("in"); io.unobserve(en.target); } });
  }, { rootMargin: "0px 0px -8% 0px", threshold: 0.05 });
  const observeReveals = () => $$("[data-reveal]:not(.in)").forEach((el) => io.observe(el));

  function bindMagnetic(root = document) {
    if (!finePointer || reduceMotion) return;
    $$("[data-magnetic]", root).forEach((el) => {
      el.addEventListener("pointermove", (e) => {
        const r = el.getBoundingClientRect();
        el.style.setProperty("--bx", `${(e.clientX - r.left - r.width / 2) * 0.22}px`);
        el.style.setProperty("--by", `${(e.clientY - r.top - r.height / 2) * 0.3}px`);
      });
      el.addEventListener("pointerleave", () => { el.style.setProperty("--bx", "0px"); el.style.setProperty("--by", "0px"); });
    });
  }

  if (finePointer) {
    document.addEventListener("pointermove", (e) => {
      const t = e.target.closest?.(".div-card, .card__media");
      if (!t) return;
      const r = t.getBoundingClientRect();
      t.style.setProperty("--mx", `${e.clientX - r.left}px`);
      t.style.setProperty("--my", `${e.clientY - r.top}px`);
    }, { passive: true });
    if (!reduceMotion) {
      const glow = $("#glow"), hero = $(".hero");
      hero.addEventListener("pointermove", (e) => {
        const x = (e.clientX / innerWidth - 0.5) * 30, y = (e.clientY / innerHeight - 0.5) * 30;
        glow.style.translate = `calc(-50% + ${x}vw) calc(-50% + ${y}vh)`;
      });
    }
  }

  // Manifesto: words light up as it scrolls through the viewport
  const mani = $("#manifesto");
  mani.innerHTML = mani.textContent.split(/\s+/).map((w) =>
    `<span class="w${/vehicle|appearance|outdoors|performance/i.test(w) ? " hl" : ""}">${esc(w)}</span>`).join(" ");
  const words = $$(".w", mani);

  const nav = $("#nav"), bar = $("#progress");
  let lastY = scrollY, ticking = false;
  function onScroll() {
    const y = scrollY;
    nav.classList.toggle("is-scrolled", y > 10);
    const drawerOpen = $("#drawer").classList.contains("on");
    nav.classList.toggle("is-hidden", !drawerOpen && y > 600 && y > lastY + 2);
    if (y < lastY - 2) nav.classList.remove("is-hidden");
    lastY = y;
    const max = document.documentElement.scrollHeight - innerHeight;
    bar.style.transform = `scaleX(${max > 0 ? y / max : 0})`;

    const r = mani.getBoundingClientRect();
    const k = reduceMotion ? 1 : Math.min(1, Math.max(0, (innerHeight * 0.85 - r.top) / (r.height + innerHeight * 0.35)));
    const lit = Math.round(k * words.length);
    words.forEach((w, i) => w.classList.toggle("on", i < lit));
    ticking = false;
  }
  addEventListener("scroll", () => { if (!ticking) { ticking = true; requestAnimationFrame(onScroll); } }, { passive: true });
  addEventListener("resize", moveSegPill);

  /* ── hero: water beading on fresh coating ─────────────────────────── */
  // Beads sit on the "paint". A cursor wipes them like a drying towel; they
  // bead back up a few seconds later. Big beads now and then run downhill,
  // and a soft studio-light reflection sweeps across.
  function initBeads() {
    const cv = $("#beads"), hero = $(".hero");
    if (!cv || !cv.getContext) return;
    const ctx = cv.getContext("2d");
    const accent = getComputedStyle(document.documentElement).getPropertyValue("--accent-rgb").trim().split(/\s+/).join(",") || "143,178,74";
    const sprites = new Map();
    let W = 0, H = 0, dpr = 1, drops = [], trails = [], raf = 0, visible = true, lastT = 0, nextSlide = 0, wiped = false;
    const rand = (a, b) => a + Math.random() * (b - a);
    const radius = () => { const k = Math.random(); return k < 0.62 ? rand(1.1, 2.6) : k < 0.9 ? rand(2.6, 4.8) : rand(4.8, 8.5); };

    function sprite(r) {
      const key = Math.max(2, Math.round(r * 2));
      if (sprites.has(key)) return sprites.get(key);
      const R = key / 2, pad = 3, box = (R + pad) * 2;
      const c = document.createElement("canvas");
      c.width = c.height = Math.ceil(box * dpr);
      const g = c.getContext("2d");
      g.scale(dpr, dpr);
      const x = R + pad, y = R + pad;
      g.fillStyle = "rgba(0,0,0,.38)";                         // contact shadow
      g.beginPath(); g.ellipse(x + R * 0.16, y + R * 0.3, R * 0.95, R * 0.88, 0, 0, Math.PI * 2); g.fill();
      const body = g.createRadialGradient(x - R * 0.3, y - R * 0.35, R * 0.05, x, y, R);
      body.addColorStop(0, "rgba(240,248,232,.14)");
      body.addColorStop(0.65, `rgba(${accent},.07)`);
      body.addColorStop(1, "rgba(214,230,200,.4)");
      g.fillStyle = body;
      g.beginPath(); g.arc(x, y, R, 0, Math.PI * 2); g.fill();
      g.strokeStyle = `rgba(${accent},.65)`;                    // light focused through the bead
      g.lineWidth = Math.max(0.5, R * 0.14);
      g.beginPath(); g.arc(x, y, R * 0.7, Math.PI * 0.18, Math.PI * 0.82); g.stroke();
      g.fillStyle = "rgba(255,255,255,.95)";                    // specular highlight
      g.beginPath(); g.ellipse(x - R * 0.36, y - R * 0.4, Math.max(0.5, R * 0.26), Math.max(0.4, R * 0.17), -0.6, 0, Math.PI * 2); g.fill();
      const out = { c, box };
      sprites.set(key, out);
      return out;
    }

    function spawn(d = {}, now = 0, fresh = false) {
      d.x = rand(0, W); d.y = rand(H * 0.12, H); d.r = radius();
      d.a = fresh ? 1 : 0; d.t = 1; d.back = 0; d.vy = 0; d.slide = false;
      d.delay = fresh ? 0 : now + rand(0, 900);
      return d;
    }

    function size() {
      const r = hero.getBoundingClientRect();
      dpr = Math.min(2, window.devicePixelRatio || 1);
      W = r.width; H = r.height;
      cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      sprites.clear();
      const n = Math.min(260, Math.round((W * H) / 6500));
      drops = Array.from({ length: n }, () => spawn({}, 0, true));
      trails = [];
    }

    function wipe(x0, y0, x1, y1, now) {
      const dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy || 1, reach = 34;
      for (const d of drops) {
        if (d.t === 0) continue;
        const k = Math.max(0, Math.min(1, ((d.x - x0) * dx + (d.y - y0) * dy) / len2));
        const ex = x0 + k * dx - d.x, ey = y0 + k * dy - d.y;
        if (ex * ex + ey * ey < (reach + d.r) ** 2) { d.t = 0; d.back = now + rand(2200, 5200); d.slide = false; }
      }
      if (!wiped) { wiped = true; $("#beadHint")?.classList.add("gone"); }
    }

    function frame(t) {
      raf = 0;
      const dt = Math.min(50, t - (lastT || t)) / 1000;
      lastT = t;
      ctx.clearRect(0, 0, W, H);

      // a big bead runs downhill every few seconds, picking up the small ones in its path
      if (!reduceMotion && t > nextSlide) {
        const big = drops.filter((d) => d.t === 1 && d.a > 0.95 && d.r > 4.6 && !d.slide);
        const d = big[Math.floor(Math.random() * big.length)];
        if (d) { d.slide = true; d.vy = 6; d.trail = { pts: [[d.x, d.y]], w: d.r * 0.9, life: 1 }; trails.push(d.trail); }
        nextSlide = t + rand(1800, 4200);
      }

      for (const d of drops) {
        if (t < d.delay) continue;
        if (d.t === 0 && d.back && t > d.back) { spawn(d, t); d.t = 1; }
        d.a += (d.t - d.a) * Math.min(1, dt * (d.t ? 2.2 : 9));
        if (d.slide) {
          d.vy = Math.min(70, d.vy + dt * 40);
          d.y += d.vy * dt; d.x += Math.sin(t / 380 + d.r) * 0.15;
          d.trail.pts.push([d.x, d.y]);
          for (const o of drops) {
            if (o !== d && o.t === 1 && !o.slide && Math.abs(o.x - d.x) < d.r && Math.abs(o.y - d.y) < d.r + o.r) {
              o.t = 0; o.a = 0; o.back = t + rand(2500, 6000); d.r = Math.min(10, d.r + o.r * 0.12);
            }
          }
          if (d.y > H + 20) { d.slide = false; d.t = 0; d.a = 0; d.back = t + rand(800, 2000); }
        }
        if (d.a < 0.02) continue;
        const s = sprite(d.r), k = 0.55 + 0.45 * d.a, b = s.box * k;
        ctx.globalAlpha = d.a;
        ctx.drawImage(s.c, d.x - b / 2, d.y - b / 2, b, b);
      }
      ctx.globalAlpha = 1;

      // wet streaks left behind by running beads
      trails = trails.filter((tr) => (tr.life -= dt * 0.12) > 0);
      ctx.lineCap = "round";
      for (const tr of trails) {
        if (tr.pts.length < 2) continue;
        ctx.strokeStyle = `rgba(210,228,196,${0.07 * tr.life})`;
        ctx.lineWidth = tr.w;
        ctx.beginPath(); ctx.moveTo(tr.pts[0][0], tr.pts[0][1]);
        for (const [x, y] of tr.pts) ctx.lineTo(x, y);
        ctx.stroke();
      }

      // studio light sweeping across the clear coat
      if (!reduceMotion) {
        const period = 9000, span = W + H;
        const p = ((t % period) / period) * (span + 600) - 300;
        const g = ctx.createLinearGradient(p - 220, 0, p + 220, H * 0.45);
        g.addColorStop(0, "rgba(255,255,255,0)");
        g.addColorStop(0.5, "rgba(230,245,214,.07)");
        g.addColorStop(1, "rgba(255,255,255,0)");
        ctx.globalCompositeOperation = "lighter";
        ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
        ctx.globalCompositeOperation = "source-over";
      }

      if (!reduceMotion && visible && !document.hidden) raf = requestAnimationFrame(frame);
    }
    const kick = () => { if (!raf) { lastT = 0; raf = requestAnimationFrame(frame); } };

    size();
    kick();
    let rt;
    addEventListener("resize", () => { clearTimeout(rt); rt = setTimeout(() => { size(); kick(); }, 200); });
    new IntersectionObserver(([e]) => { visible = e.isIntersecting; if (visible) kick(); }).observe(hero);
    document.addEventListener("visibilitychange", kick);

    let last = null;
    hero.addEventListener("pointermove", (e) => {
      const r = hero.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
      if (last && !reduceMotion) { wipe(last[0], last[1], x, y, performance.now()); kick(); }
      last = [x, y];
      if (!reduceMotion) $("#lights").style.setProperty("--lx", `${(e.clientX / innerWidth - 0.5) * -40}px`);
    }, { passive: true });
    hero.addEventListener("pointerleave", () => (last = null));
  }

  /* ── boot ─────────────────────────────────────────────────────────── */
  $("#year").textContent = new Date().getFullYear();
  renderDivisions(); renderRail(); renderChips(); renderGrid(); renderCart();
  observeReveals(); bindMagnetic(); onScroll(); initBeads();
  (document.fonts?.ready || Promise.resolve()).then(() => {
    moveSegPill();
    requestAnimationFrame(() => { document.documentElement.classList.add("is-loaded"); countUp(); });
  });
})();

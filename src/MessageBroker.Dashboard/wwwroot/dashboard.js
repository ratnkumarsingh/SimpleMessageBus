// Client-side pieces of the dashboard: the throughput chart (drawn and hovered in the browser so the
// crosshair does not need a server round trip) and copy-to-clipboard. Labels are set with textContent.

const SVG = "http://www.w3.org/2000/svg";
const charts = new WeakMap();

function svg(name, attrs, parent) {
    const el = document.createElementNS(SVG, name);
    for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
    if (parent) parent.appendChild(el);
    return el;
}

// The top of a 4-tick axis: 4 × a clean step (1, 2, 5 × 10^k), so every tick is a round number.
function niceMax(value) {
    const raw = Math.max(value, 4) / 4;
    const magnitude = Math.pow(10, Math.floor(Math.log10(raw)));
    const step = [1, 2, 5, 10].map(s => s * magnitude).find(s => s >= raw);
    return Math.max(1, step) * 4;
}

function hhmm(iso) {
    const d = new Date(iso);
    return d.toISOString().substring(11, 16);
}

function draw(state) {
    const { host, data } = state;
    const svgEl = host.querySelector("svg");
    const tip = host.querySelector(".chart-tip");
    while (svgEl.firstChild) svgEl.removeChild(svgEl.firstChild);

    const width = Math.max(host.clientWidth, 280);
    const height = 220;
    const m = { left: 44, right: 14, top: 12, bottom: 26 };
    const w = width - m.left - m.right;
    const h = height - m.top - m.bottom;
    svgEl.setAttribute("viewBox", `0 0 ${width} ${height}`);
    svgEl.setAttribute("width", width);
    svgEl.setAttribute("height", height);

    const points = data.points;
    const n = points.length;
    const max = niceMax(Math.max(0, ...points.flatMap(p => data.series.map(s => p[s.key]))));
    const x = i => m.left + (n <= 1 ? w / 2 : (i * w) / (n - 1));
    const y = v => m.top + h - (v / max) * h;

    // Recessive hairline grid, solid, with clean round ticks.
    for (let t = 0; t <= 4; t++) {
        const value = (max / 4) * t;
        const yy = Math.round(y(value)) + 0.5;
        svg("line", { x1: m.left, x2: m.left + w, y1: yy, y2: yy, class: t === 0 ? "axis" : "grid" }, svgEl);
        const label = svg("text", { x: m.left - 8, y: yy + 4, "text-anchor": "end", class: "tick" }, svgEl);
        label.textContent = value.toLocaleString("en-US");
    }
    const every = Math.max(1, Math.ceil(n / 6));
    const labelled = [];
    for (let i = 0; i < n; i += every) labelled.push(i);
    if (n > 1 && n - 1 - labelled[labelled.length - 1] >= every / 2) labelled.push(n - 1);
    for (const i of labelled) {
        const anchor = i === n - 1 && n > 1 ? "end" : "middle";
        const label = svg("text", { x: x(i), y: height - 6, "text-anchor": anchor, class: "tick" }, svgEl);
        label.textContent = hhmm(points[i].minute);
    }

    for (const s of data.series) {
        const d = points.map((p, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(1)},${y(p[s.key]).toFixed(1)}`).join("");
        svg("path", { d, class: "line", stroke: `var(${s.color})` }, svgEl);
    }
    // End dots with a surface ring mark the current minute.
    for (const s of data.series) {
        svg("circle", { cx: x(n - 1), cy: y(points[n - 1][s.key]), r: 4, class: "dot", fill: `var(${s.color})` }, svgEl);
    }

    const cross = svg("line", { x1: 0, x2: 0, y1: m.top, y2: m.top + h, class: "crosshair", visibility: "hidden" }, svgEl);
    const hit = svg("rect", { x: m.left, y: m.top, width: w, height: h, fill: "transparent" }, svgEl);

    const show = i => {
        state.index = i;
        const xx = x(i);
        cross.setAttribute("x1", xx);
        cross.setAttribute("x2", xx);
        cross.setAttribute("visibility", "visible");
        tip.replaceChildren();
        const title = document.createElement("div");
        title.className = "tip-title";
        title.textContent = `${hhmm(points[i].minute)} UTC`;
        tip.appendChild(title);
        for (const s of data.series) {
            const row = document.createElement("div");
            row.className = "tip-row";
            const key = document.createElement("span");
            key.className = "line-key";
            key.style.background = `var(${s.color})`;
            const value = document.createElement("strong");
            value.textContent = points[i][s.key].toLocaleString("en-US");
            const name = document.createElement("span");
            name.className = "tip-name";
            name.textContent = s.label;
            row.append(key, value, name);
            tip.appendChild(row);
        }
        tip.hidden = false;
        const left = Math.min(Math.max(xx + 12, 0), width - tip.offsetWidth - 4);
        tip.style.left = `${xx + 12 + tip.offsetWidth > width ? xx - tip.offsetWidth - 12 : left}px`;
        tip.style.top = `${m.top}px`;
    };
    const hide = () => {
        state.index = null;
        cross.setAttribute("visibility", "hidden");
        tip.hidden = true;
    };
    const nearest = evt => {
        const rect = svgEl.getBoundingClientRect();
        const px = evt.clientX - rect.left;
        return Math.min(n - 1, Math.max(0, Math.round(((px - m.left) / w) * (n - 1))));
    };
    hit.addEventListener("pointermove", evt => show(nearest(evt)));
    hit.addEventListener("pointerleave", hide);
    state.show = show;
    state.hide = hide;
    if (state.index !== null && state.index < n) show(state.index); else hide();
}

export function renderThroughput(host, data) {
    let state = charts.get(host);
    if (!state) {
        state = { host, data, index: null };
        charts.set(host, state);
        new ResizeObserver(() => draw(state)).observe(host);
        // Same details on keyboard focus as on hover: arrows walk the minutes.
        host.addEventListener("keydown", evt => {
            const n = state.data.points.length;
            if (evt.key === "ArrowLeft" || evt.key === "ArrowRight") {
                const step = evt.key === "ArrowLeft" ? -1 : 1;
                state.show(Math.min(n - 1, Math.max(0, (state.index ?? n - 1) + (state.index === null ? 0 : step))));
                evt.preventDefault();
            } else if (evt.key === "Escape") {
                state.hide();
            }
        });
        host.addEventListener("focus", () => state.show(state.index ?? state.data.points.length - 1));
        host.addEventListener("blur", () => state.hide());
    }
    state.data = data;
    draw(state);
}

export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}

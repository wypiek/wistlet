// Small progressive enhancements; the site works without JavaScript.
(function () {
    "use strict";

    // Dismissible announcement (remembered per announcement text).
    document.querySelectorAll("[data-dismiss-key]").forEach((el) => {
        const key = "wistlet.dismissed." + el.dataset.dismissKey;
        try { if (localStorage.getItem(key)) { el.remove(); return; } } catch { /* storage unavailable */ }
        el.querySelector("[data-dismiss]")?.addEventListener("click", () => {
            try { localStorage.setItem(key, "1"); } catch { /* ignore */ }
            el.remove();
        });
    });

    // Remaining-character counters for post and message boxes; Ctrl/Cmd+Enter submits.
    document.querySelectorAll("textarea[data-max]").forEach((ta) => {
        const counter = document.getElementById(ta.dataset.counter);
        const max = parseInt(ta.dataset.max, 10);
        const update = () => { if (counter) counter.textContent = (max - ta.value.length).toLocaleString("en-US"); };
        ta.addEventListener("input", update);
        update();
        ta.addEventListener("keydown", (e) => {
            if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) ta.form?.requestSubmit();
        });
    });

    // Confirmation prompts (data-confirm on the form or the clicked button); the
    // submit button is disabled while the request is in flight so posts aren't sent twice.
    document.addEventListener("submit", (e) => {
        const msg = e.target.dataset.confirm || e.submitter?.dataset.confirm;
        if (msg && !confirm(msg)) { e.preventDefault(); return; }
        if (e.target.classList.contains("co-composer") && e.submitter) {
            setTimeout(() => { e.submitter.disabled = true; }, 0);
        }
    });

    // Only one "More" menu open at a time; clicking elsewhere closes it.
    document.addEventListener("click", (e) => {
        document.querySelectorAll("details.co-dropdown[open]").forEach((d) => { if (!d.contains(e.target)) d.open = false; });
    });

    // Keep conversations scrolled to the newest message.
    const chat = document.querySelector(".chat");
    if (chat) chat.scrollTop = chat.scrollHeight;

    // Composer preview: the server renders the draft with the same sanitizer posts go through.
    document.querySelectorAll(".co-composer [data-preview]").forEach((button) => {
        const form = button.closest("form");
        const pane = form.querySelector(".i-preview-pane");
        const target = form.querySelector("[data-preview-target]");
        button.addEventListener("click", async () => {
            const data = new FormData(form);
            data.delete("images");
            button.disabled = true;
            try {
                const res = await fetch("/act/Preview", { method: "POST", body: data, credentials: "same-origin" });
                if (!res.ok) throw new Error(String(res.status));
                const { html } = await res.json();
                target.innerHTML = html || "<p><em>nothing to preview yet</em></p>";
                pane.hidden = false;
            } catch {
                target.textContent = "Preview failed. Try again.";
                pane.hidden = false;
            } finally {
                button.disabled = false;
            }
        });
    });

    // Composer images: a thumbnail and a description box for each chosen file (sent as "alt", in order).
    document.querySelectorAll(".co-composer input[type=file][name=images]").forEach((input) => {
        const list = input.form.querySelector("[data-new-attachments]");
        const max = parseInt(input.dataset.maxFiles || "10", 10);
        let urls = [];
        input.addEventListener("change", () => {
            urls.forEach((u) => URL.revokeObjectURL(u));
            urls = [];
            list.replaceChildren();
            const files = [...input.files];
            if (files.length > max) {
                alert(`You can attach at most ${max} images.`);
                input.value = "";
            }
            files.slice(0, max).forEach((file, i) => {
                const row = document.createElement("div");
                row.className = "i-attachment-edit";
                const img = document.createElement("img");
                img.alt = "";
                img.src = URL.createObjectURL(file);
                urls.push(img.src);
                const alt = document.createElement("textarea");
                alt.name = "alt";
                alt.rows = 2;
                alt.maxLength = 1000;
                alt.placeholder = "describe this image for people who can't see it";
                alt.setAttribute("aria-label", `Description for image ${i + 1}`);
                row.append(img, alt);
                list.append(row);
            });
            list.hidden = list.childElementCount === 0;
        });
    });

    // Lightbox for post images (cohost's attachment-lightbox): arrows/thumbnails to browse, Esc or click to close.
    document.addEventListener("click", (e) => {
        const link = e.target.closest("a[data-lightbox]");
        if (!link || e.ctrlKey || e.metaKey || e.shiftKey || !window.HTMLDialogElement) return;
        e.preventDefault();
        const items = [...link.closest(".co-attachments").querySelectorAll("a[data-lightbox]")]
            .map((a) => ({ src: a.getAttribute("href"), alt: a.dataset.alt || "" }));
        let index = items.findIndex((i) => i.src === link.getAttribute("href"));

        const dialog = document.createElement("dialog");
        dialog.className = "attachment-lightbox";
        dialog.setAttribute("aria-label", "Image viewer");
        const backdrop = document.createElement("div");
        backdrop.className = "i-backdrop";
        const container = document.createElement("div");
        container.className = "i-container";
        const top = document.createElement("div");
        top.className = "i-top";
        top.dataset.count = String(items.length);
        const img = document.createElement("img");
        img.className = "i-attachment";
        const altText = document.createElement("div");
        altText.className = "i-alt-text";
        top.append(img, altText);
        const bottom = document.createElement("div");
        bottom.className = "i-bottom";
        const thumbs = items.map((item, i) => {
            const wrap = document.createElement("div");
            wrap.className = "i-item";
            const b = document.createElement("button");
            b.type = "button";
            b.className = "i-button";
            b.setAttribute("aria-label", `Image ${i + 1}`);
            const t = document.createElement("img");
            t.className = "i-preview";
            t.src = item.src;
            t.alt = "";
            b.append(t);
            b.addEventListener("click", (ev) => { ev.stopPropagation(); show(i); });
            wrap.append(b);
            bottom.append(wrap);
            return b;
        });
        container.append(top);
        if (items.length > 1) container.append(bottom);
        dialog.append(backdrop, container);

        const show = (i) => {
            index = (i + items.length) % items.length;
            img.src = items[index].src;
            img.alt = items[index].alt;
            altText.textContent = items[index].alt;
            altText.hidden = !items[index].alt;
            thumbs.forEach((t, j) => { t.dataset.selected = String(j === index); });
        };
        dialog.addEventListener("click", (ev) => { if (!ev.target.closest(".i-button")) dialog.close(); });
        dialog.addEventListener("keydown", (ev) => {
            if (ev.key === "ArrowRight") show(index + 1);
            else if (ev.key === "ArrowLeft") show(index - 1);
        });
        dialog.addEventListener("close", () => dialog.remove());
        document.body.append(dialog);
        show(index);
        dialog.showModal();
    });

    // Like cohost, collapse very tall post bodies (over twice the window height) behind "read more".
    document.querySelectorAll(".co-post-contents > .i-post-body").forEach((body) => {
        const prose = body.querySelector(".co-prose");
        if (!prose || !window.ResizeObserver) return;

        let expanded = false;
        let shouldCollapse = false;

        const forced = document.createElement("div");
        forced.className = "i-forced-read-more co-var-post-body-bg";
        const readMore = document.createElement("button");
        readMore.type = "button";
        readMore.className = "i-read-more co-read-more-read-less";
        readMore.textContent = "read more";
        forced.append(readMore);

        const readLess = document.createElement("button");
        readLess.type = "button";
        readLess.className = "i-read-less co-read-more-read-less";
        readLess.textContent = "read less";

        const update = () => {
            const collapse = shouldCollapse && !expanded;
            body.classList.toggle("is-forced-read-more", collapse);
            if (collapse) { body.append(forced); readLess.remove(); }
            else {
                forced.remove();
                if (shouldCollapse) body.append(readLess); else readLess.remove();
            }
        };

        readMore.addEventListener("click", () => { expanded = true; update(); });
        readLess.addEventListener("click", () => {
            const fromTop = readLess.getBoundingClientRect().top;
            expanded = false;
            update();
            window.scrollTo({ top: window.scrollY + readMore.getBoundingClientRect().top - fromTop });
        });

        new ResizeObserver(() => {
            const next = prose.offsetHeight > window.innerHeight * 2;
            if (next !== shouldCollapse) { shouldCollapse = next; update(); }
        }).observe(prose);
    });
})();

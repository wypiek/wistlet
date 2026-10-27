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
        const update = () => { if (counter) counter.textContent = String(max - ta.value.length); };
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
            data.delete("image");
            data.delete("audio");
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

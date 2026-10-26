// Small progressive enhancements; the site works without JavaScript.
(function () {
    "use strict";

    // Dismissible announcement balloon (remembered per announcement text).
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

    // Confirmation prompts (data-confirm on the form or the clicked button), and
    // the 7.css spinner while a form with an upload is being sent.
    document.addEventListener("submit", (e) => {
        const msg = e.target.dataset.confirm || e.submitter?.dataset.confirm;
        if (msg && !confirm(msg)) { e.preventDefault(); return; }
        const loader = e.target.querySelector(".loader");
        if (loader) { loader.hidden = false; loader.classList.add("animate"); }
    });

    // Only one "More" menu open at a time; clicking elsewhere closes it.
    document.addEventListener("click", (e) => {
        document.querySelectorAll("details.post-more[open]").forEach((d) => { if (!d.contains(e.target)) d.open = false; });
    });

    // Keep conversations scrolled to the newest message.
    const chat = document.querySelector(".chat");
    if (chat) chat.scrollTop = chat.scrollHeight;
})();

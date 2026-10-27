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

    // Animated profile pictures and banners: paused (showing their first frame) when the device
    // asks for reduced motion or the person chose to pause them, playing while pointed at.
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
        document.querySelectorAll("img[data-still]").forEach((img) => {
            img.dataset.animated = img.getAttribute("src");
            img.src = img.dataset.still;
        });
    }
    document.querySelectorAll("img[data-animated]").forEach((img) => {
        const still = img.getAttribute("src");
        const target = img.closest("a, .project-sidebar, .co-post-preview") || img;
        const play = () => { img.src = img.dataset.animated; };
        const pause = () => { img.src = still; };
        target.addEventListener("mouseenter", play);
        target.addEventListener("mouseleave", pause);
        target.addEventListener("focusin", play);
        target.addEventListener("focusout", pause);
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

    // Live direct messages: the server pushes events over /events (Server-Sent Events). On a
    // conversation page new messages, "is typing" and "seen" appear as they happen and sending
    // doesn't reload the page; elsewhere the unread badge stays current. Without JavaScript the
    // pages work as plain forms.
    if (document.body.dataset.live === "true" && window.EventSource) {
        const chatBox = document.querySelector(".chat[data-with]");
        const status = document.querySelector(".chat-status");
        const chatForm = document.querySelector("form.chat-form");
        const withName = chatBox?.dataset.with.toLowerCase();
        const token = document.querySelector("input[name=__RequestVerificationToken]")?.value ?? "";
        const handlerUrl = (name) => location.pathname + "?handler=" + name;
        const post = (name) => fetch(handlerUrl(name), {
            method: "POST", credentials: "same-origin", headers: { RequestVerificationToken: token },
        }).catch(() => {});

        const setStatus = (text, isError = false) => {
            if (!status) return;
            status.textContent = text;
            status.classList.toggle("is-error", isError);
        };
        const lastBubble = () => chatBox?.querySelector(".bubble:last-of-type");

        const addBubble = (m) => {
            if (!chatBox || chatBox.querySelector(`.bubble[data-id="${m.id}"]`)) return;
            chatBox.querySelector(".i-empty")?.remove();
            const bubble = document.createElement("div");
            bubble.className = "bubble" + (m.mine ? " is-mine" : "");
            bubble.dataset.id = String(m.id);
            bubble.innerHTML = m.html + "<br>"; // formatted and escaped by the server
            const small = document.createElement("small");
            small.textContent = (m.mine ? "You" : chatBox.dataset.otherName) + " · just now";
            bubble.append(small);
            chatBox.append(bubble);
            chatBox.scrollTop = chatBox.scrollHeight;
        };

        const setUnread = (count) => {
            const link = document.querySelector("[data-unread-messages]");
            if (!link) return;
            let badge = link.querySelector(".i-count");
            if (count > 0) {
                if (!badge) {
                    badge = document.createElement("span");
                    badge.className = "i-count";
                    link.append(" ", badge);
                }
                badge.textContent = String(count);
            } else {
                badge?.remove();
            }
        };

        // On the conversation list, fetch it again when something changes.
        let listTimer;
        const refreshList = () => {
            const list = document.getElementById("conversation-list");
            if (!list) return;
            clearTimeout(listTimer);
            listTimer = setTimeout(async () => {
                try {
                    const html = await (await fetch("/messages", { credentials: "same-origin" })).text();
                    const fresh = new DOMParser().parseFromString(html, "text/html").getElementById("conversation-list");
                    if (fresh) list.innerHTML = fresh.innerHTML;
                } catch { /* try again on the next event */ }
            }, 300);
        };

        let pendingRead = false;
        let typingTimer;
        const events = new EventSource("/events");
        events.addEventListener("dm", (e) => {
            const m = JSON.parse(e.data);
            if (chatBox && m.with.toLowerCase() === withName) {
                addBubble(m);
                clearTimeout(typingTimer);
                setStatus("");
                if (!m.mine) {
                    if (document.visibilityState === "visible") post("Read");
                    else pendingRead = true;
                }
            } else if (!m.mine && typeof m.unread === "number") {
                setUnread(m.unread);
            }
            refreshList();
        });
        events.addEventListener("unread", (e) => setUnread(JSON.parse(e.data).count));
        events.addEventListener("read", (e) => {
            const r = JSON.parse(e.data);
            if (!chatBox || r.with.toLowerCase() !== withName) return;
            const last = lastBubble();
            if (last?.classList.contains("is-mine") && Number(last.dataset.id) <= r.upTo) setStatus("Seen");
        });
        events.addEventListener("typing", (e) => {
            if (!chatBox || JSON.parse(e.data).with.toLowerCase() !== withName) return;
            setStatus(`${chatBox.dataset.otherName} is typing…`);
            clearTimeout(typingTimer);
            typingTimer = setTimeout(() => setStatus(""), 4000);
        });

        document.addEventListener("visibilitychange", () => {
            if (pendingRead && document.visibilityState === "visible") {
                pendingRead = false;
                post("Read");
            }
        });

        if (chatForm) {
            const text = chatForm.querySelector("textarea");
            let lastTyping = 0;
            text.addEventListener("input", () => {
                if (text.value.trim() && Date.now() - lastTyping > 3000) {
                    lastTyping = Date.now();
                    post("Typing");
                }
            });
            chatForm.addEventListener("submit", async (e) => {
                e.preventDefault();
                if (!text.value.trim()) return;
                const button = chatForm.querySelector("button[type=submit]");
                button.disabled = true;
                try {
                    const res = await fetch(handlerUrl("Send"), { method: "POST", body: new FormData(chatForm), credentials: "same-origin" });
                    const data = await res.json().catch(() => ({}));
                    if (!res.ok) {
                        setStatus(data.error || (res.status === 429 ? "Slow down a little, then try again." : "The message couldn't be sent."), true);
                    } else {
                        addBubble(data);
                        setStatus("");
                        text.value = "";
                        text.dispatchEvent(new Event("input"));
                        lastTyping = 0;
                    }
                } catch {
                    chatForm.submit(); // fall back to a normal post
                } finally {
                    button.disabled = false;
                    text.focus();
                }
            });
        }
    }

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

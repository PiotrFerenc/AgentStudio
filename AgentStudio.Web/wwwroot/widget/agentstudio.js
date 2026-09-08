/* AgentStudio embeddable chat widget.
   Usage:
     <script src="https://host/widget/agentstudio.js"></script>
     <agent-studio-chat api-url="https://host" agent-id="GUID" version="1" api-key="ask_..."></agent-studio-chat>
*/
(function () {
  class AgentStudioChat extends HTMLElement {
    connectedCallback() {
      const apiUrl = this.getAttribute("api-url") || location.origin;
      const agentId = this.getAttribute("agent-id");
      const version = this.getAttribute("version") || "1";
      const apiKey = this.getAttribute("api-key") || "";

      this.attachShadow({ mode: "open" });
      this.shadowRoot.innerHTML = `
        <style>
          :host { display:block; font-family: system-ui, sans-serif; }
          .box { border:1px solid #dee2e6; border-radius:10px; background:#fff; max-width:480px; }
          .head { padding:.6rem 1rem; background:#4c6ef5; color:#fff; border-radius:10px 10px 0 0; font-weight:600; }
          .msgs { height:320px; overflow-y:auto; padding:.75rem 1rem; }
          .m { margin-bottom:.6rem; }
          .m .r { font-size:.65rem; text-transform:uppercase; color:#868e96; font-weight:700; }
          .m.user .r { color:#4c6ef5; }
          .m.assistant .r { color:#2b8a3e; }
          .c { white-space:pre-wrap; font-size:.9rem; }
          .row { display:flex; gap:.5rem; padding:.6rem; border-top:1px solid #e9ecef; }
          input { flex:1; padding:.45rem .6rem; border:1px solid #cbd2dc; border-radius:6px; }
          button { background:#4c6ef5; color:#fff; border:0; border-radius:6px; padding:.45rem .9rem; cursor:pointer; }
        </style>
        <div class="box">
          <div class="head">AgentStudio Chat</div>
          <div class="msgs"></div>
          <div class="row">
            <input type="text" placeholder="Type a message..." />
            <button>Send</button>
          </div>
        </div>`;

      const msgs = this.shadowRoot.querySelector(".msgs");
      const input = this.shadowRoot.querySelector("input");
      const button = this.shadowRoot.querySelector("button");

      let conversationId = sessionStorage.getItem(`agentstudio-conv-${agentId}`) || null;

      const addMsg = (role, content) => {
        const div = document.createElement("div");
        div.className = `m ${role}`;
        div.innerHTML = `<div class="r">${role}</div><div class="c"></div>`;
        div.querySelector(".c").textContent = content;
        msgs.appendChild(div);
        msgs.scrollTop = msgs.scrollHeight;
        return div.querySelector(".c");
      };

      const postFallback = async (text, target) => {
        const resp = await fetch(`${apiUrl}/api/agents/${agentId}/versions/${version}/conversations`, {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-Agent-Api-Key": apiKey },
          body: JSON.stringify({ message: text, conversationId })
        });
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const data = await resp.json();
        conversationId = data.conversationId;
        sessionStorage.setItem(`agentstudio-conv-${agentId}`, conversationId);
        target.textContent = data.reply;
      };

      // Streamed send via the /stream SSE endpoint. EventSource cannot POST, so parse the
      // event stream manually from fetch + ReadableStream, rendering deltas incrementally.
      const sendStream = async (text, target) => {
        const resp = await fetch(`${apiUrl}/api/agents/${agentId}/versions/${version}/stream`, {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-Agent-Api-Key": apiKey },
          body: JSON.stringify({ message: text, conversationId })
        });
        if (!resp.ok || !resp.body) throw new Error(`HTTP ${resp.status}`);

        const reader = resp.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        let acc = "";

        const handleEvent = (raw) => {
          const lines = raw.split("\n");
          let event = "message", data = "";
          for (const line of lines) {
            if (line.startsWith("event:")) event = line.slice(6).trim();
            else if (line.startsWith("data:")) data += line.slice(5).trim();
          }
          if (event === "conversation" && data) {
            try {
              const parsed = JSON.parse(data);
              if (parsed.conversationId) {
                conversationId = parsed.conversationId;
                sessionStorage.setItem(`agentstudio-conv-${agentId}`, conversationId);
              }
            } catch { /* ignore malformed event */ }
          } else if (event === "message" && data) {
            try {
              const parsed = JSON.parse(data);
              if (parsed.delta) {
                acc += parsed.delta;
                target.textContent = acc;
                msgs.scrollTop = msgs.scrollHeight;
              }
            } catch { /* ignore malformed event */ }
          }
        };

        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          let idx;
          while ((idx = buffer.indexOf("\n\n")) >= 0) {
            handleEvent(buffer.slice(0, idx));
            buffer = buffer.slice(idx + 2);
          }
        }
        if (buffer.trim()) handleEvent(buffer);
        if (!acc) target.textContent = "";
      };

      const send = async () => {
        const text = input.value.trim();
        if (!text) return;
        input.value = "";
        addMsg("user", text);
        const target = addMsg("assistant", "…");

        try {
          await sendStream(text, target);
          if (target.textContent === "") target.textContent = "(empty reply)";
        } catch (err) {
          try {
            await postFallback(text, target);
          } catch (err2) {
            target.textContent = `[error] ${err2.message}`;
          }
        }
      };

      button.addEventListener("click", send);
      input.addEventListener("keydown", e => { if (e.key === "Enter") send(); });
    }
  }
  customElements.define("agent-studio-chat", AgentStudioChat);
})();

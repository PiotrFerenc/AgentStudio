/* AgentStudio embeddable form widget.
   Usage:
     <script src="https://host/widget/agentstudio-form.js"></script>
     <agent-studio-form api-url="https://host" agent-id="GUID" version="1" api-key="ask_..." height="640"></agent-studio-form>
*/
(function () {
  class AgentStudioForm extends HTMLElement {
    connectedCallback() {
      const apiUrl = this.getAttribute("api-url") || location.origin;
      const agentId = this.getAttribute("agent-id");
      const version = this.getAttribute("version") || "1";
      const apiKey = this.getAttribute("api-key") || "";
      const height = this.getAttribute("height") || "640";

      // Embed the existing /run page in an iframe rather than reimplementing the wizard/
      // validation/conditional-visibility logic in vanilla JS: that page is already fully
      // functional, this way there's nothing here that can drift out of sync with it.
      const src = `${apiUrl}/run/${agentId}/${version}?key=${encodeURIComponent(apiKey)}`;

      this.attachShadow({ mode: "open" });
      this.shadowRoot.innerHTML = `
        <style>
          :host { display:block; font-family: system-ui, sans-serif; }
          .box {
            border:1px solid #dee2e6;
            border-radius:10px;
            background:#fff;
            width:100%;
            overflow:hidden;
          }
          iframe { display:block; width:100%; border:0; }
        </style>
        <div class="box">
          <iframe src="${src}" height="${height}" title="AgentStudio form"></iframe>
        </div>`;
    }
  }
  customElements.define("agent-studio-form", AgentStudioForm);
})();

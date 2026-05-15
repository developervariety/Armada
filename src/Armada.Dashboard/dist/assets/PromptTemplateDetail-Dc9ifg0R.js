import{u as V,ah as L,b as J,r as n,i as G,bl as O,j as e,af as q,bm as Y,bk as _}from"./index-ByrCkrts.js";import{A as $}from"./ActionMenu-Di7NBXPs.js";import{J as H}from"./JsonViewer-Ba4TCFPV.js";import{S as v}from"./StatusBadge-BB2164qE.js";import{C as K}from"./ConfirmDialog-DbqleS8T.js";import{C as Q}from"./CopyButton-CEATLavV.js";import{E as I}from"./ErrorModal-DVQjeR_f.js";const W=[{label:"Mission Context",params:[{name:"{MissionId}",description:"Mission identifier"},{name:"{MissionTitle}",description:"Mission title"},{name:"{MissionDescription}",description:"Full mission description"},{name:"{MissionPersona}",description:"Persona assigned to this mission"},{name:"{VoyageId}",description:"Parent voyage identifier"},{name:"{BranchName}",description:"Git branch for this mission"}]},{label:"Vessel Context",params:[{name:"{VesselId}",description:"Vessel identifier"},{name:"{VesselName}",description:"Vessel display name"},{name:"{DefaultBranch}",description:"Default branch (e.g. main)"},{name:"{ProjectContext}",description:"User-supplied project description"},{name:"{StyleGuide}",description:"User-supplied style guide"},{name:"{ModelContext}",description:"Agent-accumulated context"},{name:"{FleetId}",description:"Parent fleet identifier"}]},{label:"Captain Context",params:[{name:"{CaptainId}",description:"Captain identifier"},{name:"{CaptainName}",description:"Captain display name"},{name:"{CaptainInstructions}",description:"User-supplied captain instructions"}]},{label:"Pipeline Context",params:[{name:"{PersonaPrompt}",description:"Resolved persona prompt text"},{name:"{PreviousStageDiff}",description:"Diff from prior pipeline stage"},{name:"{ExistingClaudeMd}",description:"Contents of repo's existing CLAUDE.md"}]},{label:"System",params:[{name:"{Timestamp}",description:"Current UTC timestamp"}]}];function ne(){const{t,formatDateTime:j}=V(),{name:l}=L(),M=J(),C=n.useRef(null),[s,u]=n.useState(null),[z,y]=n.useState(!0),[x,r]=n.useState(""),[f,N]=n.useState(!1),{pushToast:S}=G(),[d,m]=n.useState(""),[w,c]=n.useState(""),[k,o]=n.useState(!1),[h,D]=n.useState({open:!1,title:"",data:null}),[p,g]=n.useState({open:!1,title:"",message:"",onConfirm:()=>{}}),P=n.useCallback(async()=>{if(l)try{y(!0);const a=!s,i=await O(l);u(i),m(i.content),c(i.description??""),o(!1),a&&r("")}catch{r(t("Failed to load prompt template."))}finally{y(!1)}},[l,t]);n.useEffect(()=>{P()},[P]);function R(a){m(a),o(!0)}function U(a){c(a),o(!0)}async function E(){if(!(!l||!s))try{N(!0);const a=await Y(l,{content:d,description:w});u(a),m(a.content),c(a.description??""),o(!1),S("success",t("Template saved."))}catch{r(t("Save failed."))}finally{N(!1)}}function T(){!s||!s.isBuiltIn||g({open:!0,title:t("Reset to Default"),message:t('Reset "{{name}}" to its built-in default content? Your customizations will be lost.',{name:s.name}),onConfirm:async()=>{g(a=>({...a,open:!1}));try{const a=await _(s.name);u(a),m(a.content),c(a.description??""),o(!1),S("success",t("Template reset to default."))}catch{r(t("Reset failed."))}}})}function B(a){const i=C.current;if(!i)return;const b=i.selectionStart,A=i.selectionEnd,F=d.substring(0,b)+a+d.substring(A);m(F),o(!0),requestAnimationFrame(()=>{i.focus(),i.selectionStart=b+a.length,i.selectionEnd=b+a.length})}return z?e.jsx("p",{className:"text-dim",children:t("Loading...")}):x&&!s?e.jsx(I,{error:x,onClose:()=>r("")}):s?e.jsxs("div",{children:[e.jsxs("div",{className:"breadcrumb",children:[e.jsx(q,{to:"/prompt-templates",children:t("Prompt Templates")})," ",e.jsx("span",{className:"breadcrumb-sep",children:">"})," ",e.jsx("span",{children:s.name})]}),e.jsxs("div",{className:"detail-header",children:[e.jsx("h2",{children:s.name}),e.jsxs("div",{className:"inline-actions",children:[e.jsx(v,{status:s.category}),s.isBuiltIn&&e.jsx(v,{status:"Built-in"}),e.jsx($,{id:`template-${s.name}`,items:[{label:"View JSON",onClick:()=>D({open:!0,title:t("Template: {{name}}",{name:s.name}),data:s})},...s.isBuiltIn?[{label:"Reset to Default",danger:!0,onClick:T}]:[]]})]})]}),e.jsx(I,{error:x,onClose:()=>r("")}),e.jsx(H,{open:h.open,title:h.title,data:h.data,onClose:()=>D({open:!1,title:"",data:null})}),e.jsx(K,{open:p.open,title:p.title,message:p.message,onConfirm:p.onConfirm,onCancel:()=>g(a=>({...a,open:!1}))}),e.jsx("style",{children:`
        .template-editor-layout {
          display: grid;
          grid-template-columns: 1fr 340px;
          gap: 1.5rem;
          margin-top: 1rem;
        }
        @media (max-width: 900px) {
          .template-editor-layout {
            grid-template-columns: 1fr;
          }
        }
        .template-editor-panel {
          display: flex;
          flex-direction: column;
          gap: 0.75rem;
        }
        .template-editor-textarea {
          width: 100%;
          min-height: 500px;
          font-family: 'SF Mono', 'Fira Code', 'Cascadia Code', Consolas, monospace;
          font-size: 0.875em;
          line-height: 1.5;
          padding: 12px;
          border: 1px solid var(--border);
          border-radius: 6px;
          background: var(--input-bg);
          color: var(--text);
          resize: vertical;
          tab-size: 2;
        }
        .template-editor-textarea:focus {
          outline: none;
          border-color: var(--accent);
          box-shadow: 0 0 0 2px rgba(59, 130, 246, 0.15);
        }
        .template-description-input {
          width: 100%;
          padding: 8px 12px;
          border: 1px solid var(--border);
          border-radius: 6px;
          background: var(--input-bg);
          color: var(--text);
          font-size: 0.9em;
        }
        .template-description-input:focus {
          outline: none;
          border-color: var(--accent);
          box-shadow: 0 0 0 2px rgba(59, 130, 246, 0.15);
        }
        .template-param-panel {
          border: 1px solid var(--border);
          border-radius: 6px;
          background: var(--bg-card);
          padding: 1rem;
          max-height: 700px;
          overflow-y: auto;
        }
        .template-param-panel h4 {
          margin: 0 0 0.75rem 0;
          font-size: 0.95em;
          color: var(--text-dim);
        }
        .template-param-group {
          margin-bottom: 1rem;
        }
        .template-param-group:last-child {
          margin-bottom: 0;
        }
        .template-param-group-label {
          font-size: 0.8em;
          font-weight: 600;
          text-transform: uppercase;
          letter-spacing: 0.05em;
          color: var(--text-dim);
          margin-bottom: 0.4rem;
          padding-bottom: 0.25rem;
          border-bottom: 1px solid var(--border);
        }
        .template-param-item {
          display: flex;
          align-items: baseline;
          gap: 0.5rem;
          padding: 4px 0;
          cursor: pointer;
          border-radius: 3px;
          transition: background 0.15s;
        }
        .template-param-item:hover {
          background: var(--bg-hover);
        }
        .template-param-name {
          font-family: 'SF Mono', 'Fira Code', 'Cascadia Code', Consolas, monospace;
          font-size: 0.8em;
          color: var(--accent);
          white-space: nowrap;
          flex-shrink: 0;
        }
        .template-param-desc {
          font-size: 0.78em;
          color: var(--text-dim);
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
        }
        .template-editor-actions {
          display: flex;
          gap: 0.5rem;
          align-items: center;
        }
        .template-char-count {
          font-size: 0.8em;
          color: var(--text-dim);
          margin-left: auto;
        }
        .template-dirty-indicator {
          display: inline-block;
          width: 8px;
          height: 8px;
          border-radius: 50%;
          background: #f0a040;
          margin-left: 0.25rem;
        }
      `}),e.jsxs("div",{className:"detail-grid",children:[e.jsxs("div",{className:"detail-field",children:[e.jsx("span",{className:"detail-label",children:t("ID")}),e.jsxs("span",{className:"id-display",children:[e.jsx("span",{className:"mono",children:s.id}),e.jsx(Q,{text:s.id})]})]}),e.jsxs("div",{className:"detail-field",children:[e.jsx("span",{className:"detail-label",children:t("Active")}),e.jsx(v,{status:s.active!==!1?"Active":"Inactive"})]}),e.jsxs("div",{className:"detail-field",children:[e.jsx("span",{className:"detail-label",children:t("Created")}),e.jsx("span",{children:j(s.createdUtc)})]}),e.jsxs("div",{className:"detail-field",children:[e.jsx("span",{className:"detail-label",children:t("Last Updated")}),e.jsx("span",{children:s.lastUpdateUtc?j(s.lastUpdateUtc):"-"})]})]}),e.jsxs("div",{className:"template-editor-layout",children:[e.jsxs("div",{className:"template-editor-panel",children:[e.jsxs("label",{style:{fontSize:"0.85em",color:"var(--text-dim)"},children:[t("Description"),e.jsx("input",{type:"text",className:"template-description-input",value:w,onChange:a=>U(a.target.value),placeholder:t("Template description...")})]}),e.jsxs("div",{style:{display:"flex",justifyContent:"space-between",alignItems:"center"},children:[e.jsxs("label",{style:{fontSize:"0.85em",color:"var(--text-dim)",margin:0},children:[t("Template Content"),k&&e.jsx("span",{className:"template-dirty-indicator",title:t("Unsaved changes")})]}),e.jsxs("span",{className:"template-char-count",children:[d.length," ",t("characters")]})]}),e.jsx("textarea",{ref:C,className:"template-editor-textarea",value:d,onChange:a=>R(a.target.value),rows:30,spellCheck:!1}),e.jsxs("div",{className:"template-editor-actions",children:[e.jsx("button",{className:"btn btn-primary",onClick:E,disabled:f||!k,children:t(f?"Saving...":"Save")}),s.isBuiltIn&&e.jsx("button",{className:"btn",onClick:T,disabled:f,children:t("Reset to Default")}),e.jsx("button",{className:"btn",onClick:()=>M("/prompt-templates"),children:t("Back")})]})]}),e.jsxs("div",{className:"template-param-panel",children:[e.jsx("h4",{children:t("Parameters")}),e.jsx("p",{style:{fontSize:"0.78em",color:"var(--text-dim)",margin:"0 0 0.75rem 0"},children:t("Click a parameter to insert it at the cursor position.")}),W.map(a=>e.jsxs("div",{className:"template-param-group",children:[e.jsx("div",{className:"template-param-group-label",children:t(a.label)}),a.params.map(i=>e.jsxs("div",{className:"template-param-item",onClick:()=>B(i.name),title:t("Insert {{name}} -- {{description}}",{name:i.name,description:t(i.description)}),children:[e.jsx("span",{className:"template-param-name",children:i.name}),e.jsx("span",{className:"template-param-desc",children:t(i.description)})]},i.name))]},a.label))]})]})]}):e.jsx("p",{className:"text-dim",children:t("Template not found.")})}export{ne as default};

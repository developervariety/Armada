import{u as H,ai as K,b as Q,r as l,i as W,bg as X,j as e,ag as Z,bo as F,bi as ee,bj as te}from"./index-DPafG8fc.js";import{A as ae}from"./ActionMenu-CQrKxoao.js";import{J as se}from"./JsonViewer-eNjx-iR9.js";import{S as N}from"./StatusBadge-C91mCHBu.js";import{C as ie}from"./ConfirmDialog-B9oq19rp.js";import{C as ne}from"./CopyButton-BrJYmxJo.js";import{E as B}from"./ErrorModal-Cf1YzZtF.js";import{g as re}from"./duplicates-DJVvItkr.js";const le=[{label:"Mission Context",params:[{name:"{MissionId}",description:"Mission identifier"},{name:"{MissionTitle}",description:"Mission title"},{name:"{MissionDescription}",description:"Full mission description"},{name:"{MissionPersona}",description:"Persona assigned to this mission"},{name:"{VoyageId}",description:"Parent voyage identifier"},{name:"{BranchName}",description:"Git branch for this mission"}]},{label:"Vessel Context",params:[{name:"{VesselId}",description:"Vessel identifier"},{name:"{VesselName}",description:"Vessel display name"},{name:"{DefaultBranch}",description:"Default branch (e.g. main)"},{name:"{ProjectContext}",description:"User-supplied project description"},{name:"{StyleGuide}",description:"User-supplied style guide"},{name:"{ModelContext}",description:"Agent-accumulated context"},{name:"{FleetId}",description:"Parent fleet identifier"}]},{label:"Captain Context",params:[{name:"{CaptainId}",description:"Captain identifier"},{name:"{CaptainName}",description:"Captain display name"},{name:"{CaptainInstructions}",description:"User-supplied captain instructions"}]},{label:"Pipeline Context",params:[{name:"{PersonaPrompt}",description:"Resolved persona prompt text"},{name:"{PreviousStageDiff}",description:"Diff from prior pipeline stage"},{name:"{ExistingClaudeMd}",description:"Contents of repo's existing CLAUDE.md"}]},{label:"System",params:[{name:"{Timestamp}",description:"Current UTC timestamp"}]}],oe=["mission","persona","structure","commit","landing","agent"];function ge(){const{t:a,formatDateTime:E}=H(),{name:p}=K(),T=Q(),z=l.useRef(null),o=!p,[s,u]=l.useState(null),[V,S]=l.useState(!0),[P,r]=l.useState(""),[w,f]=l.useState(!1),{pushToast:v}=W(),[g,b]=l.useState(""),[C,j]=l.useState("mission"),[c,d]=l.useState(""),[I,x]=l.useState(""),[M,m]=l.useState(!1),[D,R]=l.useState({open:!1,title:"",data:null}),[y,k]=l.useState({open:!1,title:"",message:"",onConfirm:()=>{}}),U=l.useCallback(async()=>{if(o){u(null),b(""),j("mission"),d(""),x(""),m(!1),r(""),S(!1);return}if(p)try{S(!0);const t=await X(p);u(t),b(t.name),j(t.category),d(t.content),x(t.description??""),m(!1),r("")}catch(t){u(null),r(t instanceof Error?t.message:a("Failed to load prompt template."))}finally{S(!1)}},[o,p,a]);l.useEffect(()=>{U()},[U]);function L(t){b(t),m(!0)}function O(t){j(t),m(!0)}function q(t){d(t),m(!0)}function G(t){x(t),m(!0)}async function J(){const t=g.trim(),n=C.trim(),h=I.trim();if(o){if(!t){r(a("Template name is required."));return}if(!n){r(a("Template category is required."));return}if(!c.trim()){r(a("Template content is required."));return}try{f(!0);const i=await F({name:t,category:n,content:c,description:h||void 0,active:!0});u(i),b(i.name),j(i.category),d(i.content),x(i.description??""),m(!1),r(""),v("success",a('Template "{{name}}" created.',{name:i.name})),T(`/prompt-templates/${encodeURIComponent(i.name)}`,{replace:!0})}catch(i){r(i instanceof Error?i.message:a("Create failed."))}finally{f(!1)}return}if(!(!p||!s))try{f(!0);const i=await ee(p,{content:c,description:h||void 0});u(i),b(i.name),j(i.category),d(i.content),x(i.description??""),m(!1),r(""),v("success",a("Template saved."))}catch(i){r(i instanceof Error?i.message:a("Save failed."))}finally{f(!1)}}async function _(){if(s)try{f(!0);const t=await F(re(s));v("success",a('Template "{{name}}" duplicated.',{name:t.name})),T(`/prompt-templates/${encodeURIComponent(t.name)}`)}catch(t){r(t instanceof Error?t.message:a("Duplicate failed."))}finally{f(!1)}}function A(){!s||!s.isBuiltIn||k({open:!0,title:a("Reset to Default"),message:a('Reset "{{name}}" to its built-in default content? Your customizations will be lost.',{name:s.name}),onConfirm:async()=>{k(t=>({...t,open:!1}));try{const t=await te(s.name);u(t),d(t.content),x(t.description??""),m(!1),v("success",a("Template reset to default."))}catch{r(a("Reset failed."))}}})}function $(t){const n=z.current;if(!n)return;const h=n.selectionStart,i=n.selectionEnd,Y=c.substring(0,h)+t+c.substring(i);d(Y),m(!0),requestAnimationFrame(()=>{n.focus(),n.selectionStart=h+t.length,n.selectionEnd=h+t.length})}return V?e.jsx("p",{className:"text-dim",children:a("Loading...")}):!o&&P&&!s?e.jsx(B,{error:P,onClose:()=>r("")}):!o&&!s?e.jsx("p",{className:"text-dim",children:a("Template not found.")}):e.jsxs("div",{children:[e.jsxs("div",{className:"breadcrumb",children:[e.jsx(Z,{to:"/prompt-templates",children:a("Prompt Templates")})," ",e.jsx("span",{className:"breadcrumb-sep",children:">"})," ",e.jsx("span",{children:o?a("Create"):g})]}),e.jsxs("div",{className:"detail-header",children:[e.jsx("h2",{children:o?a("Create Prompt Template"):g}),e.jsx("div",{className:"inline-actions",children:o?e.jsx(N,{status:C||"mission"}):e.jsxs(e.Fragment,{children:[e.jsx(N,{status:s.category}),s.isBuiltIn&&e.jsx(N,{status:"Built-in"}),e.jsx(ae,{id:`template-${s.name}`,items:[{label:"Duplicate",onClick:()=>void _()},{label:"View JSON",onClick:()=>R({open:!0,title:a("Template: {{name}}",{name:s.name}),data:s})},...s.isBuiltIn?[{label:"Reset to Default",danger:!0,onClick:A}]:[]]})]})})]}),e.jsx(B,{error:P,onClose:()=>r("")}),e.jsx(se,{open:D.open,title:D.title,data:D.data,onClose:()=>R({open:!1,title:"",data:null})}),e.jsx(ie,{open:y.open,title:y.title,message:y.message,onConfirm:y.onConfirm,onCancel:()=>k(t=>({...t,open:!1}))}),e.jsx("style",{children:`
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
        .template-meta-field {
          display: grid;
          gap: 0.35rem;
        }
        .template-meta-label {
          font-size: 0.85em;
          color: var(--text-dim);
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
      `}),e.jsx("div",{className:"detail-grid",children:o?e.jsxs(e.Fragment,{children:[e.jsxs("label",{className:"detail-field template-meta-field",children:[e.jsx("span",{className:"detail-label",children:a("Name")}),e.jsx("input",{className:"template-description-input",value:g,onChange:t=>L(t.target.value),placeholder:a("mission.rules.custom")})]}),e.jsxs("label",{className:"detail-field template-meta-field",children:[e.jsx("span",{className:"detail-label",children:a("Category")}),e.jsx("input",{className:"template-description-input",list:"prompt-template-category-options",value:C,onChange:t=>O(t.target.value),placeholder:a("mission")}),e.jsx("datalist",{id:"prompt-template-category-options",children:oe.map(t=>e.jsx("option",{value:t},t))})]}),e.jsxs("div",{className:"detail-field",children:[e.jsx("span",{className:"detail-label",children:a("Type")}),e.jsx("span",{children:a("Custom template")})]})]}):e.jsxs(e.Fragment,{children:[e.jsxs("div",{className:"detail-field",children:[e.jsx("span",{className:"detail-label",children:a("ID")}),e.jsxs("span",{className:"id-display",children:[e.jsx("span",{className:"mono",children:s.id}),e.jsx(ne,{text:s.id})]})]}),e.jsxs("div",{className:"detail-field",children:[e.jsx("span",{className:"detail-label",children:a("Active")}),e.jsx(N,{status:s.active!==!1?"Active":"Inactive"})]}),e.jsxs("div",{className:"detail-field",children:[e.jsx("span",{className:"detail-label",children:a("Created")}),e.jsx("span",{children:E(s.createdUtc)})]}),e.jsxs("div",{className:"detail-field",children:[e.jsx("span",{className:"detail-label",children:a("Last Updated")}),e.jsx("span",{children:s.lastUpdateUtc?E(s.lastUpdateUtc):"-"})]})]})}),e.jsxs("div",{className:"template-editor-layout",children:[e.jsxs("div",{className:"template-editor-panel",children:[e.jsxs("label",{style:{fontSize:"0.85em",color:"var(--text-dim)"},children:[a("Description"),e.jsx("input",{type:"text",className:"template-description-input",value:I,onChange:t=>G(t.target.value),placeholder:a("Template description...")})]}),e.jsxs("div",{style:{display:"flex",justifyContent:"space-between",alignItems:"center"},children:[e.jsxs("label",{style:{fontSize:"0.85em",color:"var(--text-dim)",margin:0},children:[a("Template Content"),M&&e.jsx("span",{className:"template-dirty-indicator",title:a("Unsaved changes")})]}),e.jsxs("span",{className:"template-char-count",children:[c.length," ",a("characters")]})]}),e.jsx("textarea",{ref:z,className:"template-editor-textarea",value:c,onChange:t=>q(t.target.value),rows:30,spellCheck:!1}),e.jsxs("div",{className:"template-editor-actions",children:[e.jsx("button",{className:"btn btn-primary",onClick:J,disabled:w||!M||o&&(!g.trim()||!C.trim()||!c.trim()),children:a(w?"Saving...":"Save")}),(s==null?void 0:s.isBuiltIn)&&e.jsx("button",{className:"btn",onClick:A,disabled:w,children:a("Reset to Default")}),e.jsx("button",{className:"btn",onClick:()=>T("/prompt-templates"),children:a("Back")})]})]}),e.jsxs("div",{className:"template-param-panel",children:[e.jsx("h4",{children:a("Parameters")}),e.jsx("p",{style:{fontSize:"0.78em",color:"var(--text-dim)",margin:"0 0 0.75rem 0"},children:a("Click a parameter to insert it at the cursor position.")}),le.map(t=>e.jsxs("div",{className:"template-param-group",children:[e.jsx("div",{className:"template-param-group-label",children:a(t.label)}),t.params.map(n=>e.jsxs("div",{className:"template-param-item",onClick:()=>$(n.name),title:a("Insert {{name}} -- {{description}}",{name:n.name,description:a(n.description)}),children:[e.jsx("span",{className:"template-param-name",children:n.name}),e.jsx("span",{className:"template-param-desc",children:a(n.description)})]},n.name))]},t.label))]})]})]})}export{ge as default};

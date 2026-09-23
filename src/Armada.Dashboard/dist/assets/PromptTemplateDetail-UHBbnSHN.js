import{i as e,n as t,r as n,s as r}from"./LocaleContext-JtHbApia.js";import{D as i,Lr as a,ki as o,nn as ee}from"./client-KjYiLXaE.js";import{n as te}from"./AuthContext-BlRucukC.js";import{f as s,h as c,l,m as u}from"./index-Bdzv75pn.js";import{t as ne}from"./CopyButton-BcB9qZOW.js";import{t as d}from"./PageHeader-Cf30mAOk.js";import{t as f}from"./ErrorModal-B4cw94ts.js";import{t as p}from"./ConfirmDialog-BVCk6Tga.js";import{t as m}from"./ActionMenu-Bvfe6C01.js";import{t as h}from"./JsonViewer-Cmj7QzvJ.js";import{t as g}from"./StatusBadge-BivHKlKE.js";import{c as _}from"./duplicates-DYeBzws0.js";import{a as v,i as y,n as b,o as re,s as x,t as S}from"./ScopeBadge-CiDZt-r6.js";var C=r(e(),1),w=n(),ie=[{label:`Mission Context`,params:[{name:`{MissionId}`,description:`Mission identifier`},{name:`{MissionTitle}`,description:`Mission title`},{name:`{MissionDescription}`,description:`Full mission description`},{name:`{MissionPersona}`,description:`Persona assigned to this mission`},{name:`{VoyageId}`,description:`Parent voyage identifier`},{name:`{BranchName}`,description:`Git branch for this mission`}]},{label:`Vessel Context`,params:[{name:`{VesselId}`,description:`Vessel identifier`},{name:`{VesselName}`,description:`Vessel display name`},{name:`{DefaultBranch}`,description:`Default branch (e.g. main)`},{name:`{ProjectContext}`,description:`User-supplied project description`},{name:`{StyleGuide}`,description:`User-supplied style guide`},{name:`{ModelContext}`,description:`Agent-accumulated context`},{name:`{FleetId}`,description:`Parent fleet identifier`}]},{label:`Captain Context`,params:[{name:`{CaptainId}`,description:`Captain identifier`},{name:`{CaptainName}`,description:`Captain display name`},{name:`{CaptainInstructions}`,description:`User-supplied captain instructions`}]},{label:`Pipeline Context`,params:[{name:`{PersonaPrompt}`,description:`Resolved persona prompt text`},{name:`{PreviousStageDiff}`,description:`Diff from prior pipeline stage`},{name:`{ExistingClaudeMd}`,description:`Contents of repo's existing CLAUDE.md`}]},{label:`System`,params:[{name:`{Timestamp}`,description:`Current UTC timestamp`}]}],ae=[`mission`,`persona`,`structure`,`commit`,`landing`,`agent`];function T(){let{t:e,formatDateTime:n}=t(),{name:r}=c(),T=u(),E=(0,C.useRef)(null),D=!r,O=x(te()),[k,A]=(0,C.useState)(null),j=D?v(O,b.promptTemplates):k?y(O,k,b.promptTemplates):!1,[oe,M]=(0,C.useState)(!0),[N,P]=(0,C.useState)(``),[F,I]=(0,C.useState)(!1),{pushToast:L}=l(),[R,z]=(0,C.useState)(``),[B,V]=(0,C.useState)(`mission`),[H,U]=(0,C.useState)(``),[W,G]=(0,C.useState)(``),[K,q]=(0,C.useState)(!1),[J,Y]=(0,C.useState)({open:!1,title:``,data:null}),[X,Z]=(0,C.useState)({open:!1,title:``,message:``,onConfirm:()=>{}}),Q=(0,C.useCallback)(async()=>{if(D){A(null),z(``),V(`mission`),U(``),G(``),q(!1),P(``),M(!1);return}if(r)try{M(!0);let e=await ee(r);A(e),z(e.name),V(e.category),U(e.content),G(e.description??``),q(!1),P(``)}catch(t){A(null),P(t instanceof Error?t.message:e(`Failed to load prompt template.`))}finally{M(!1)}},[D,r,e]);(0,C.useEffect)(()=>{Q()},[Q]);function se(e){z(e),q(!0)}function ce(e){V(e),q(!0)}function le(e){U(e),q(!0)}function ue(e){G(e),q(!0)}async function de(){let t=R.trim(),n=B.trim(),a=W.trim();if(D){if(!t){P(e(`Template name is required.`));return}if(!n){P(e(`Template category is required.`));return}if(!H.trim()){P(e(`Template content is required.`));return}try{I(!0);let r=await i({name:t,category:n,content:H,description:a||void 0,active:!0});A(r),z(r.name),V(r.category),U(r.content),G(r.description??``),q(!1),P(``),L(`success`,e(`Template "{{name}}" created.`,{name:r.name})),T(`/prompt-templates/${encodeURIComponent(r.name)}`,{replace:!0})}catch(t){P(t instanceof Error?t.message:e(`Create failed.`))}finally{I(!1)}return}if(!(!r||!k))try{I(!0);let t=await o(r,{content:H,description:a||void 0});A(t),z(t.name),V(t.category),U(t.content),G(t.description??``),q(!1),P(``),L(`success`,e(`Template saved.`))}catch(t){P(t instanceof Error?t.message:e(`Save failed.`))}finally{I(!1)}}async function fe(){if(k)try{I(!0);let t=await i({..._(k),ownershipScope:re(O,k.ownershipScope)});L(`success`,e(`Template "{{name}}" duplicated.`,{name:t.name})),T(`/prompt-templates/${encodeURIComponent(t.name)}`)}catch(t){P(t instanceof Error?t.message:e(`Duplicate failed.`))}finally{I(!1)}}function $(){!k||!k.isBuiltIn||Z({open:!0,title:e(`Reset to Default`),message:e(`Reset "{{name}}" to its built-in default content? Your customizations will be lost.`,{name:k.name}),onConfirm:async()=>{Z(e=>({...e,open:!1}));try{let t=await a(k.name);A(t),U(t.content),G(t.description??``),q(!1),L(`success`,e(`Template reset to default.`))}catch{P(e(`Reset failed.`))}}})}function pe(e){let t=E.current;if(!t)return;let n=t.selectionStart,r=t.selectionEnd,i=H.substring(0,n)+e+H.substring(r);U(i),q(!0),requestAnimationFrame(()=>{t.focus(),t.selectionStart=n+e.length,t.selectionEnd=n+e.length})}return oe?(0,w.jsx)(`p`,{className:`text-dim`,children:e(`Loading...`)}):!D&&N&&!k?(0,w.jsx)(f,{error:N,onClose:()=>P(``)}):!D&&!k?(0,w.jsx)(`p`,{className:`text-dim`,children:e(`Template not found.`)}):(0,w.jsxs)(`div`,{children:[(0,w.jsx)(d,{breadcrumb:(0,w.jsxs)(w.Fragment,{children:[(0,w.jsx)(s,{to:`/prompt-templates`,children:e(`Prompt Templates`)}),` `,(0,w.jsx)(`span`,{className:`breadcrumb-sep`,children:`>`}),` `,(0,w.jsx)(`span`,{children:D?e(`Create`):R})]}),title:D?e(`Create Prompt Template`):R,actions:(0,w.jsx)(w.Fragment,{children:D?(0,w.jsx)(g,{status:B||`mission`}):(0,w.jsxs)(w.Fragment,{children:[(0,w.jsx)(g,{status:k.category}),k.isBuiltIn&&(0,w.jsx)(g,{status:`Built-in`}),(0,w.jsx)(S,{scope:k.ownershipScope}),(0,w.jsx)(m,{id:`template-${k.name}`,items:[...v(O,b.promptTemplates)?[{label:`Duplicate`,onClick:()=>void fe()}]:[],{label:`View JSON`,onClick:()=>Y({open:!0,title:e(`Template: {{name}}`,{name:k.name}),data:k})},...k.isBuiltIn&&j?[{label:`Reset to Default`,danger:!0,onClick:$}]:[]]})]})})}),(0,w.jsx)(f,{error:N,onClose:()=>P(``)}),(0,w.jsx)(h,{open:J.open,title:J.title,data:J.data,onClose:()=>Y({open:!1,title:``,data:null})}),(0,w.jsx)(p,{open:X.open,title:X.title,message:X.message,onConfirm:X.onConfirm,onCancel:()=>Z(e=>({...e,open:!1}))}),(0,w.jsx)(`style`,{children:`
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
      `}),(0,w.jsx)(`div`,{className:`detail-grid`,children:D?(0,w.jsxs)(w.Fragment,{children:[(0,w.jsxs)(`label`,{className:`detail-field template-meta-field`,children:[(0,w.jsx)(`span`,{className:`detail-label`,children:e(`Name`)}),(0,w.jsx)(`input`,{className:`template-description-input`,value:R,onChange:e=>se(e.target.value),placeholder:e(`mission.rules.custom`)})]}),(0,w.jsxs)(`label`,{className:`detail-field template-meta-field`,children:[(0,w.jsx)(`span`,{className:`detail-label`,children:e(`Category`)}),(0,w.jsx)(`input`,{className:`template-description-input`,list:`prompt-template-category-options`,value:B,onChange:e=>ce(e.target.value),placeholder:e(`mission`)}),(0,w.jsx)(`datalist`,{id:`prompt-template-category-options`,children:ae.map(e=>(0,w.jsx)(`option`,{value:e},e))})]}),(0,w.jsxs)(`div`,{className:`detail-field`,children:[(0,w.jsx)(`span`,{className:`detail-label`,children:e(`Type`)}),(0,w.jsx)(`span`,{children:e(`Custom template`)})]})]}):(0,w.jsxs)(w.Fragment,{children:[(0,w.jsxs)(`div`,{className:`detail-field`,children:[(0,w.jsx)(`span`,{className:`detail-label`,children:e(`ID`)}),(0,w.jsxs)(`span`,{className:`id-display`,children:[(0,w.jsx)(`span`,{className:`mono`,children:k.id}),(0,w.jsx)(ne,{text:k.id})]})]}),(0,w.jsxs)(`div`,{className:`detail-field`,children:[(0,w.jsx)(`span`,{className:`detail-label`,children:e(`Active`)}),(0,w.jsx)(g,{status:k.active===!1?`Inactive`:`Active`})]}),(0,w.jsxs)(`div`,{className:`detail-field`,children:[(0,w.jsx)(`span`,{className:`detail-label`,children:e(`Created`)}),(0,w.jsx)(`span`,{children:n(k.createdUtc)})]}),(0,w.jsxs)(`div`,{className:`detail-field`,children:[(0,w.jsx)(`span`,{className:`detail-label`,children:e(`Last Updated`)}),(0,w.jsx)(`span`,{children:k.lastUpdateUtc?n(k.lastUpdateUtc):`-`})]})]})}),(0,w.jsxs)(`div`,{className:`template-editor-layout`,children:[(0,w.jsxs)(`div`,{className:`template-editor-panel`,children:[(0,w.jsxs)(`label`,{style:{fontSize:`0.85em`,color:`var(--text-dim)`},children:[e(`Description`),(0,w.jsx)(`input`,{type:`text`,className:`template-description-input`,value:W,onChange:e=>ue(e.target.value),placeholder:e(`Template description...`)})]}),(0,w.jsxs)(`div`,{style:{display:`flex`,justifyContent:`space-between`,alignItems:`center`},children:[(0,w.jsxs)(`label`,{style:{fontSize:`0.85em`,color:`var(--text-dim)`,margin:0},children:[e(`Template Content`),K&&(0,w.jsx)(`span`,{className:`template-dirty-indicator`,title:e(`Unsaved changes`)})]}),(0,w.jsxs)(`span`,{className:`template-char-count`,children:[H.length,` `,e(`characters`)]})]}),(0,w.jsx)(`textarea`,{ref:E,className:`template-editor-textarea`,value:H,onChange:e=>le(e.target.value),rows:30,spellCheck:!1}),(0,w.jsxs)(`div`,{className:`template-editor-actions`,children:[(0,w.jsx)(`button`,{className:`btn btn-primary`,onClick:de,disabled:F||!K||!j||D&&(!R.trim()||!B.trim()||!H.trim()),children:e(F?`Saving...`:`Save`)}),k?.isBuiltIn&&(0,w.jsx)(`button`,{className:`btn`,onClick:$,disabled:F,children:e(`Reset to Default`)}),(0,w.jsx)(`button`,{className:`btn`,onClick:()=>T(`/prompt-templates`),children:e(`Back`)})]})]}),(0,w.jsxs)(`div`,{className:`template-param-panel`,children:[(0,w.jsx)(`h4`,{children:e(`Parameters`)}),(0,w.jsx)(`p`,{style:{fontSize:`0.78em`,color:`var(--text-dim)`,margin:`0 0 0.75rem 0`},children:e(`Click a parameter to insert it at the cursor position.`)}),ie.map(t=>(0,w.jsxs)(`div`,{className:`template-param-group`,children:[(0,w.jsx)(`div`,{className:`template-param-group-label`,children:e(t.label)}),t.params.map(t=>(0,w.jsxs)(`div`,{className:`template-param-item`,onClick:()=>pe(t.name),title:e(`Insert {{name}} -- {{description}}`,{name:t.name,description:e(t.description)}),children:[(0,w.jsx)(`span`,{className:`template-param-name`,children:t.name}),(0,w.jsx)(`span`,{className:`template-param-desc`,children:e(t.description)})]},t.name))]},t.label))]})]})]})}export{T as default};
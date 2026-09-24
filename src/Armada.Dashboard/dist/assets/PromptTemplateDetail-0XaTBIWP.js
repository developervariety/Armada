import{i as e,n as t,r as n,s as r}from"./LocaleContext-JtHbApia.js";import{D as i,Ni as a,Vr as o,nn as ee}from"./client-BO-JCSd1.js";import{n as te}from"./AuthContext-De7icotL.js";import{f as s,h as ne,l as c,m as l}from"./index-LTse-wIA.js";import{t as re}from"./CopyButton-BcB9qZOW.js";import{t as u}from"./PageHeader-Cf30mAOk.js";import{t as d}from"./ErrorModal-B4cw94ts.js";import{t as f}from"./ConfirmDialog-BVCk6Tga.js";import{t as p}from"./ActionMenu-CERZIMr9.js";import{t as m}from"./JsonViewer-Cmj7QzvJ.js";import{t as h}from"./StatusBadge-BivHKlKE.js";import{t as g}from"./useLatestRequest-BkjDuNcE.js";import{c as _}from"./duplicates-DYeBzws0.js";import{a as v,i as ie,n as y,o as b,s as x,t as ae}from"./ScopeBadge-CiDZt-r6.js";var S=r(e(),1),C=n(),oe=[{label:`Mission Context`,params:[{name:`{MissionId}`,description:`Mission identifier`},{name:`{MissionTitle}`,description:`Mission title`},{name:`{MissionDescription}`,description:`Full mission description`},{name:`{MissionPersona}`,description:`Persona assigned to this mission`},{name:`{VoyageId}`,description:`Parent voyage identifier`},{name:`{BranchName}`,description:`Git branch for this mission`}]},{label:`Vessel Context`,params:[{name:`{VesselId}`,description:`Vessel identifier`},{name:`{VesselName}`,description:`Vessel display name`},{name:`{DefaultBranch}`,description:`Default branch (e.g. main)`},{name:`{ProjectContext}`,description:`User-supplied project description`},{name:`{StyleGuide}`,description:`User-supplied style guide`},{name:`{ModelContext}`,description:`Agent-accumulated context`},{name:`{FleetId}`,description:`Parent fleet identifier`}]},{label:`Captain Context`,params:[{name:`{CaptainId}`,description:`Captain identifier`},{name:`{CaptainName}`,description:`Captain display name`},{name:`{CaptainInstructions}`,description:`User-supplied captain instructions`}]},{label:`Pipeline Context`,params:[{name:`{PersonaPrompt}`,description:`Resolved persona prompt text`},{name:`{PreviousStageDiff}`,description:`Diff from prior pipeline stage`},{name:`{ExistingClaudeMd}`,description:`Contents of repo's existing CLAUDE.md`}]},{label:`System`,params:[{name:`{Timestamp}`,description:`Current UTC timestamp`}]}],se=[`mission`,`persona`,`structure`,`commit`,`landing`,`agent`];function w(){let{t:e,formatDateTime:n}=t(),{name:r}=ne(),w=l(),T=(0,S.useRef)(null),E=!r,D=x(te()),[O,k]=(0,S.useState)(null),A=E?v(D,y.promptTemplates):O?ie(D,O,y.promptTemplates):!1,[ce,j]=(0,S.useState)(!0),[M,N]=(0,S.useState)(``),[P,F]=(0,S.useState)(!1),{pushToast:I}=c(),[L,R]=(0,S.useState)(``),[z,B]=(0,S.useState)(`mission`),[V,H]=(0,S.useState)(``),[U,W]=(0,S.useState)(``),[G,K]=(0,S.useState)(!1),[q,J]=(0,S.useState)({open:!1,title:``,data:null}),[Y,X]=(0,S.useState)({open:!1,title:``,message:``,onConfirm:()=>{}}),Z=g(),Q=(0,S.useCallback)(async()=>{let t=Z.begin(E?``:r);if(E){k(null),R(``),B(`mission`),H(``),W(``),K(!1),N(``),j(!1);return}if(r)try{j(!0);let e=await ee(r);if(!t.isCurrent())return;k(e),R(e.name),B(e.category),H(e.content),W(e.description??``),K(!1),N(``)}catch(n){if(!t.isCurrent())return;k(null),N(n instanceof Error?n.message:e(`Failed to load prompt template.`))}finally{t.isCurrent()&&j(!1)}},[Z,E,r,e]);(0,S.useEffect)(()=>{Q()},[Q]);function le(e){R(e),K(!0)}function ue(e){B(e),K(!0)}function de(e){H(e),K(!0)}function fe(e){W(e),K(!0)}async function pe(){let t=L.trim(),n=z.trim(),o=U.trim();if(E){if(!t){N(e(`Template name is required.`));return}if(!n){N(e(`Template category is required.`));return}if(!V.trim()){N(e(`Template content is required.`));return}try{F(!0);let r=await i({name:t,category:n,content:V,description:o||void 0,active:!0});k(r),R(r.name),B(r.category),H(r.content),W(r.description??``),K(!1),N(``),I(`success`,e(`Template "{{name}}" created.`,{name:r.name})),w(`/prompt-templates/${encodeURIComponent(r.name)}`,{replace:!0})}catch(t){N(t instanceof Error?t.message:e(`Create failed.`))}finally{F(!1)}return}if(!(!r||!O))try{F(!0);let t=await a(r,{content:V,description:o||void 0});k(t),R(t.name),B(t.category),H(t.content),W(t.description??``),K(!1),N(``),I(`success`,e(`Template saved.`))}catch(t){N(t instanceof Error?t.message:e(`Save failed.`))}finally{F(!1)}}async function me(){if(O)try{F(!0);let t=await i({..._(O),ownershipScope:b(D,O.ownershipScope)});I(`success`,e(`Template "{{name}}" duplicated.`,{name:t.name})),w(`/prompt-templates/${encodeURIComponent(t.name)}`)}catch(t){N(t instanceof Error?t.message:e(`Duplicate failed.`))}finally{F(!1)}}function $(){!O||!O.isBuiltIn||X({open:!0,title:e(`Reset to Default`),message:e(`Reset "{{name}}" to its built-in default content? Your customizations will be lost.`,{name:O.name}),onConfirm:async()=>{X(e=>({...e,open:!1}));try{let t=await o(O.name);k(t),H(t.content),W(t.description??``),K(!1),I(`success`,e(`Template reset to default.`))}catch{N(e(`Reset failed.`))}}})}function he(e){let t=T.current;if(!t)return;let n=t.selectionStart,r=t.selectionEnd,i=V.substring(0,n)+e+V.substring(r);H(i),K(!0),requestAnimationFrame(()=>{t.focus(),t.selectionStart=n+e.length,t.selectionEnd=n+e.length})}return ce?(0,C.jsx)(`p`,{className:`text-dim`,children:e(`Loading...`)}):!E&&M&&!O?(0,C.jsx)(d,{error:M,onClose:()=>N(``)}):!E&&!O?(0,C.jsx)(`p`,{className:`text-dim`,children:e(`Template not found.`)}):(0,C.jsxs)(`div`,{children:[(0,C.jsx)(u,{breadcrumb:(0,C.jsxs)(C.Fragment,{children:[(0,C.jsx)(s,{to:`/prompt-templates`,children:e(`Prompt Templates`)}),` `,(0,C.jsx)(`span`,{className:`breadcrumb-sep`,children:`>`}),` `,(0,C.jsx)(`span`,{children:E?e(`Create`):L})]}),title:E?e(`Create Prompt Template`):L,actions:(0,C.jsx)(C.Fragment,{children:E?(0,C.jsx)(h,{status:z||`mission`}):(0,C.jsxs)(C.Fragment,{children:[(0,C.jsx)(h,{status:O.category}),O.isBuiltIn&&(0,C.jsx)(h,{status:`Built-in`}),(0,C.jsx)(ae,{scope:O.ownershipScope}),(0,C.jsx)(p,{id:`template-${O.name}`,items:[...v(D,y.promptTemplates)?[{label:`Duplicate`,onClick:()=>void me()}]:[],{label:`View JSON`,onClick:()=>J({open:!0,title:e(`Template: {{name}}`,{name:O.name}),data:O})},...O.isBuiltIn&&A?[{label:`Reset to Default`,danger:!0,onClick:$}]:[]]})]})})}),(0,C.jsx)(d,{error:M,onClose:()=>N(``)}),(0,C.jsx)(m,{open:q.open,title:q.title,data:q.data,onClose:()=>J({open:!1,title:``,data:null})}),(0,C.jsx)(f,{open:Y.open,title:Y.title,message:Y.message,onConfirm:Y.onConfirm,onCancel:()=>X(e=>({...e,open:!1}))}),(0,C.jsx)(`style`,{children:`
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
      `}),(0,C.jsx)(`div`,{className:`detail-grid`,children:E?(0,C.jsxs)(C.Fragment,{children:[(0,C.jsxs)(`label`,{className:`detail-field template-meta-field`,children:[(0,C.jsx)(`span`,{className:`detail-label`,children:e(`Name`)}),(0,C.jsx)(`input`,{className:`template-description-input`,value:L,onChange:e=>le(e.target.value),placeholder:e(`mission.rules.custom`)})]}),(0,C.jsxs)(`label`,{className:`detail-field template-meta-field`,children:[(0,C.jsx)(`span`,{className:`detail-label`,children:e(`Category`)}),(0,C.jsx)(`input`,{className:`template-description-input`,list:`prompt-template-category-options`,value:z,onChange:e=>ue(e.target.value),placeholder:e(`mission`)}),(0,C.jsx)(`datalist`,{id:`prompt-template-category-options`,children:se.map(e=>(0,C.jsx)(`option`,{value:e},e))})]}),(0,C.jsxs)(`div`,{className:`detail-field`,children:[(0,C.jsx)(`span`,{className:`detail-label`,children:e(`Type`)}),(0,C.jsx)(`span`,{children:e(`Custom template`)})]})]}):(0,C.jsxs)(C.Fragment,{children:[(0,C.jsxs)(`div`,{className:`detail-field`,children:[(0,C.jsx)(`span`,{className:`detail-label`,children:e(`ID`)}),(0,C.jsxs)(`span`,{className:`id-display`,children:[(0,C.jsx)(`span`,{className:`mono`,children:O.id}),(0,C.jsx)(re,{text:O.id})]})]}),(0,C.jsxs)(`div`,{className:`detail-field`,children:[(0,C.jsx)(`span`,{className:`detail-label`,children:e(`Active`)}),(0,C.jsx)(h,{status:O.active===!1?`Inactive`:`Active`})]}),(0,C.jsxs)(`div`,{className:`detail-field`,children:[(0,C.jsx)(`span`,{className:`detail-label`,children:e(`Created`)}),(0,C.jsx)(`span`,{children:n(O.createdUtc)})]}),(0,C.jsxs)(`div`,{className:`detail-field`,children:[(0,C.jsx)(`span`,{className:`detail-label`,children:e(`Last Updated`)}),(0,C.jsx)(`span`,{children:O.lastUpdateUtc?n(O.lastUpdateUtc):`-`})]})]})}),(0,C.jsxs)(`div`,{className:`template-editor-layout`,children:[(0,C.jsxs)(`div`,{className:`template-editor-panel`,children:[(0,C.jsxs)(`label`,{style:{fontSize:`0.85em`,color:`var(--text-dim)`},children:[e(`Description`),(0,C.jsx)(`input`,{type:`text`,className:`template-description-input`,value:U,onChange:e=>fe(e.target.value),placeholder:e(`Template description...`)})]}),(0,C.jsxs)(`div`,{style:{display:`flex`,justifyContent:`space-between`,alignItems:`center`},children:[(0,C.jsxs)(`label`,{style:{fontSize:`0.85em`,color:`var(--text-dim)`,margin:0},children:[e(`Template Content`),G&&(0,C.jsx)(`span`,{className:`template-dirty-indicator`,title:e(`Unsaved changes`)})]}),(0,C.jsxs)(`span`,{className:`template-char-count`,children:[V.length,` `,e(`characters`)]})]}),(0,C.jsx)(`textarea`,{ref:T,className:`template-editor-textarea`,value:V,onChange:e=>de(e.target.value),rows:30,spellCheck:!1}),(0,C.jsxs)(`div`,{className:`template-editor-actions`,children:[(0,C.jsx)(`button`,{className:`btn btn-primary`,onClick:pe,disabled:P||!G||!A||E&&(!L.trim()||!z.trim()||!V.trim()),children:e(P?`Saving...`:`Save`)}),O?.isBuiltIn&&(0,C.jsx)(`button`,{className:`btn`,onClick:$,disabled:P,children:e(`Reset to Default`)}),(0,C.jsx)(`button`,{className:`btn`,onClick:()=>w(`/prompt-templates`),children:e(`Back`)})]})]}),(0,C.jsxs)(`div`,{className:`template-param-panel`,children:[(0,C.jsx)(`h4`,{children:e(`Parameters`)}),(0,C.jsx)(`p`,{style:{fontSize:`0.78em`,color:`var(--text-dim)`,margin:`0 0 0.75rem 0`},children:e(`Click a parameter to insert it at the cursor position.`)}),oe.map(t=>(0,C.jsxs)(`div`,{className:`template-param-group`,children:[(0,C.jsx)(`div`,{className:`template-param-group-label`,children:e(t.label)}),t.params.map(t=>(0,C.jsxs)(`div`,{className:`template-param-item`,onClick:()=>he(t.name),title:e(`Insert {{name}} -- {{description}}`,{name:t.name,description:e(t.description)}),children:[(0,C.jsx)(`span`,{className:`template-param-name`,children:t.name}),(0,C.jsx)(`span`,{className:`template-param-desc`,children:e(t.description)})]},t.name))]},t.label))]})]})]})}export{w as default};
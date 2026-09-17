import{i as e,n as t,r as n,s as r}from"./LocaleContext-JtHbApia.js";import{Ei as i,O as a,Pr as o,nn as s}from"./client-zE44UGmT.js";import{n as c}from"./AuthContext-DLIciv_8.js";import{f as ee,h as te,l,m as u}from"./index-BSOCfACB.js";import{t as d}from"./CopyButton-BcB9qZOW.js";import{t as ne}from"./PageHeader-Cf30mAOk.js";import{t as f}from"./ErrorModal-B4cw94ts.js";import{t as re}from"./ConfirmDialog-BVCk6Tga.js";import{t as ie}from"./ActionMenu-DclNRydq.js";import{t as ae}from"./JsonViewer-Cmj7QzvJ.js";import{t as p}from"./StatusBadge-BivHKlKE.js";import{c as m}from"./duplicates-DYeBzws0.js";import{a as h,i as g,n as _,o as v,s as y,t as b}from"./ScopeBadge-CiDZt-r6.js";var x=r(e(),1),S=n(),oe=[{label:`Mission Context`,params:[{name:`{MissionId}`,description:`Mission identifier`},{name:`{MissionTitle}`,description:`Mission title`},{name:`{MissionDescription}`,description:`Full mission description`},{name:`{MissionPersona}`,description:`Persona assigned to this mission`},{name:`{VoyageId}`,description:`Parent voyage identifier`},{name:`{BranchName}`,description:`Git branch for this mission`}]},{label:`Vessel Context`,params:[{name:`{VesselId}`,description:`Vessel identifier`},{name:`{VesselName}`,description:`Vessel display name`},{name:`{DefaultBranch}`,description:`Default branch (e.g. main)`},{name:`{ProjectContext}`,description:`User-supplied project description`},{name:`{StyleGuide}`,description:`User-supplied style guide`},{name:`{ModelContext}`,description:`Agent-accumulated context`},{name:`{FleetId}`,description:`Parent fleet identifier`}]},{label:`Captain Context`,params:[{name:`{CaptainId}`,description:`Captain identifier`},{name:`{CaptainName}`,description:`Captain display name`},{name:`{CaptainInstructions}`,description:`User-supplied captain instructions`}]},{label:`Pipeline Context`,params:[{name:`{PersonaPrompt}`,description:`Resolved persona prompt text`},{name:`{PreviousStageDiff}`,description:`Diff from prior pipeline stage`},{name:`{ExistingClaudeMd}`,description:`Contents of repo's existing CLAUDE.md`}]},{label:`System`,params:[{name:`{Timestamp}`,description:`Current UTC timestamp`}]}],se=[`mission`,`persona`,`structure`,`commit`,`landing`,`agent`];function C(){let{t:e,formatDateTime:n}=t(),{name:r}=te(),C=u(),w=(0,x.useRef)(null),T=!r,E=y(c()),[D,O]=(0,x.useState)(null),k=T?h(E,_.promptTemplates):D?g(E,D,_.promptTemplates):!1,[A,j]=(0,x.useState)(!0),[M,N]=(0,x.useState)(``),[P,F]=(0,x.useState)(!1),{pushToast:I}=l(),[L,R]=(0,x.useState)(``),[z,B]=(0,x.useState)(`mission`),[V,H]=(0,x.useState)(``),[U,W]=(0,x.useState)(``),[G,K]=(0,x.useState)(!1),[q,J]=(0,x.useState)({open:!1,title:``,data:null}),[Y,X]=(0,x.useState)({open:!1,title:``,message:``,onConfirm:()=>{}}),Z=(0,x.useCallback)(async()=>{if(T){O(null),R(``),B(`mission`),H(``),W(``),K(!1),N(``),j(!1);return}if(r)try{j(!0);let e=await s(r);O(e),R(e.name),B(e.category),H(e.content),W(e.description??``),K(!1),N(``)}catch(t){O(null),N(t instanceof Error?t.message:e(`Failed to load prompt template.`))}finally{j(!1)}},[T,r,e]);(0,x.useEffect)(()=>{Z()},[Z]);function Q(e){R(e),K(!0)}function ce(e){B(e),K(!0)}function le(e){H(e),K(!0)}function ue(e){W(e),K(!0)}async function de(){let t=L.trim(),n=z.trim(),o=U.trim();if(T){if(!t){N(e(`Template name is required.`));return}if(!n){N(e(`Template category is required.`));return}if(!V.trim()){N(e(`Template content is required.`));return}try{F(!0);let r=await a({name:t,category:n,content:V,description:o||void 0,active:!0});O(r),R(r.name),B(r.category),H(r.content),W(r.description??``),K(!1),N(``),I(`success`,e(`Template "{{name}}" created.`,{name:r.name})),C(`/prompt-templates/${encodeURIComponent(r.name)}`,{replace:!0})}catch(t){N(t instanceof Error?t.message:e(`Create failed.`))}finally{F(!1)}return}if(!(!r||!D))try{F(!0);let t=await i(r,{content:V,description:o||void 0});O(t),R(t.name),B(t.category),H(t.content),W(t.description??``),K(!1),N(``),I(`success`,e(`Template saved.`))}catch(t){N(t instanceof Error?t.message:e(`Save failed.`))}finally{F(!1)}}async function fe(){if(D)try{F(!0);let t=await a({...m(D),ownershipScope:v(E,D.ownershipScope)});I(`success`,e(`Template "{{name}}" duplicated.`,{name:t.name})),C(`/prompt-templates/${encodeURIComponent(t.name)}`)}catch(t){N(t instanceof Error?t.message:e(`Duplicate failed.`))}finally{F(!1)}}function $(){!D||!D.isBuiltIn||X({open:!0,title:e(`Reset to Default`),message:e(`Reset "{{name}}" to its built-in default content? Your customizations will be lost.`,{name:D.name}),onConfirm:async()=>{X(e=>({...e,open:!1}));try{let t=await o(D.name);O(t),H(t.content),W(t.description??``),K(!1),I(`success`,e(`Template reset to default.`))}catch{N(e(`Reset failed.`))}}})}function pe(e){let t=w.current;if(!t)return;let n=t.selectionStart,r=t.selectionEnd,i=V.substring(0,n)+e+V.substring(r);H(i),K(!0),requestAnimationFrame(()=>{t.focus(),t.selectionStart=n+e.length,t.selectionEnd=n+e.length})}return A?(0,S.jsx)(`p`,{className:`text-dim`,children:e(`Loading...`)}):!T&&M&&!D?(0,S.jsx)(f,{error:M,onClose:()=>N(``)}):!T&&!D?(0,S.jsx)(`p`,{className:`text-dim`,children:e(`Template not found.`)}):(0,S.jsxs)(`div`,{children:[(0,S.jsx)(ne,{breadcrumb:(0,S.jsxs)(S.Fragment,{children:[(0,S.jsx)(ee,{to:`/prompt-templates`,children:e(`Prompt Templates`)}),` `,(0,S.jsx)(`span`,{className:`breadcrumb-sep`,children:`>`}),` `,(0,S.jsx)(`span`,{children:T?e(`Create`):L})]}),title:T?e(`Create Prompt Template`):L,actions:(0,S.jsx)(S.Fragment,{children:T?(0,S.jsx)(p,{status:z||`mission`}):(0,S.jsxs)(S.Fragment,{children:[(0,S.jsx)(p,{status:D.category}),D.isBuiltIn&&(0,S.jsx)(p,{status:`Built-in`}),(0,S.jsx)(b,{scope:D.ownershipScope}),(0,S.jsx)(ie,{id:`template-${D.name}`,items:[...h(E,_.promptTemplates)?[{label:`Duplicate`,onClick:()=>void fe()}]:[],{label:`View JSON`,onClick:()=>J({open:!0,title:e(`Template: {{name}}`,{name:D.name}),data:D})},...D.isBuiltIn&&k?[{label:`Reset to Default`,danger:!0,onClick:$}]:[]]})]})})}),(0,S.jsx)(f,{error:M,onClose:()=>N(``)}),(0,S.jsx)(ae,{open:q.open,title:q.title,data:q.data,onClose:()=>J({open:!1,title:``,data:null})}),(0,S.jsx)(re,{open:Y.open,title:Y.title,message:Y.message,onConfirm:Y.onConfirm,onCancel:()=>X(e=>({...e,open:!1}))}),(0,S.jsx)(`style`,{children:`
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
      `}),(0,S.jsx)(`div`,{className:`detail-grid`,children:T?(0,S.jsxs)(S.Fragment,{children:[(0,S.jsxs)(`label`,{className:`detail-field template-meta-field`,children:[(0,S.jsx)(`span`,{className:`detail-label`,children:e(`Name`)}),(0,S.jsx)(`input`,{className:`template-description-input`,value:L,onChange:e=>Q(e.target.value),placeholder:e(`mission.rules.custom`)})]}),(0,S.jsxs)(`label`,{className:`detail-field template-meta-field`,children:[(0,S.jsx)(`span`,{className:`detail-label`,children:e(`Category`)}),(0,S.jsx)(`input`,{className:`template-description-input`,list:`prompt-template-category-options`,value:z,onChange:e=>ce(e.target.value),placeholder:e(`mission`)}),(0,S.jsx)(`datalist`,{id:`prompt-template-category-options`,children:se.map(e=>(0,S.jsx)(`option`,{value:e},e))})]}),(0,S.jsxs)(`div`,{className:`detail-field`,children:[(0,S.jsx)(`span`,{className:`detail-label`,children:e(`Type`)}),(0,S.jsx)(`span`,{children:e(`Custom template`)})]})]}):(0,S.jsxs)(S.Fragment,{children:[(0,S.jsxs)(`div`,{className:`detail-field`,children:[(0,S.jsx)(`span`,{className:`detail-label`,children:e(`ID`)}),(0,S.jsxs)(`span`,{className:`id-display`,children:[(0,S.jsx)(`span`,{className:`mono`,children:D.id}),(0,S.jsx)(d,{text:D.id})]})]}),(0,S.jsxs)(`div`,{className:`detail-field`,children:[(0,S.jsx)(`span`,{className:`detail-label`,children:e(`Active`)}),(0,S.jsx)(p,{status:D.active===!1?`Inactive`:`Active`})]}),(0,S.jsxs)(`div`,{className:`detail-field`,children:[(0,S.jsx)(`span`,{className:`detail-label`,children:e(`Created`)}),(0,S.jsx)(`span`,{children:n(D.createdUtc)})]}),(0,S.jsxs)(`div`,{className:`detail-field`,children:[(0,S.jsx)(`span`,{className:`detail-label`,children:e(`Last Updated`)}),(0,S.jsx)(`span`,{children:D.lastUpdateUtc?n(D.lastUpdateUtc):`-`})]})]})}),(0,S.jsxs)(`div`,{className:`template-editor-layout`,children:[(0,S.jsxs)(`div`,{className:`template-editor-panel`,children:[(0,S.jsxs)(`label`,{style:{fontSize:`0.85em`,color:`var(--text-dim)`},children:[e(`Description`),(0,S.jsx)(`input`,{type:`text`,className:`template-description-input`,value:U,onChange:e=>ue(e.target.value),placeholder:e(`Template description...`)})]}),(0,S.jsxs)(`div`,{style:{display:`flex`,justifyContent:`space-between`,alignItems:`center`},children:[(0,S.jsxs)(`label`,{style:{fontSize:`0.85em`,color:`var(--text-dim)`,margin:0},children:[e(`Template Content`),G&&(0,S.jsx)(`span`,{className:`template-dirty-indicator`,title:e(`Unsaved changes`)})]}),(0,S.jsxs)(`span`,{className:`template-char-count`,children:[V.length,` `,e(`characters`)]})]}),(0,S.jsx)(`textarea`,{ref:w,className:`template-editor-textarea`,value:V,onChange:e=>le(e.target.value),rows:30,spellCheck:!1}),(0,S.jsxs)(`div`,{className:`template-editor-actions`,children:[(0,S.jsx)(`button`,{className:`btn btn-primary`,onClick:de,disabled:P||!G||!k||T&&(!L.trim()||!z.trim()||!V.trim()),children:e(P?`Saving...`:`Save`)}),D?.isBuiltIn&&(0,S.jsx)(`button`,{className:`btn`,onClick:$,disabled:P,children:e(`Reset to Default`)}),(0,S.jsx)(`button`,{className:`btn`,onClick:()=>C(`/prompt-templates`),children:e(`Back`)})]})]}),(0,S.jsxs)(`div`,{className:`template-param-panel`,children:[(0,S.jsx)(`h4`,{children:e(`Parameters`)}),(0,S.jsx)(`p`,{style:{fontSize:`0.78em`,color:`var(--text-dim)`,margin:`0 0 0.75rem 0`},children:e(`Click a parameter to insert it at the cursor position.`)}),oe.map(t=>(0,S.jsxs)(`div`,{className:`template-param-group`,children:[(0,S.jsx)(`div`,{className:`template-param-group-label`,children:e(t.label)}),t.params.map(t=>(0,S.jsxs)(`div`,{className:`template-param-item`,onClick:()=>pe(t.name),title:e(`Insert {{name}} -- {{description}}`,{name:t.name,description:e(t.description)}),children:[(0,S.jsx)(`span`,{className:`template-param-name`,children:t.name}),(0,S.jsx)(`span`,{className:`template-param-desc`,children:e(t.description)})]},t.name))]},t.label))]})]})]})}export{C as default};
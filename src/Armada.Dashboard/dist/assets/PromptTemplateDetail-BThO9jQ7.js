import{a as e,l as t,n,o as r}from"./LocaleContext-hRCpAw7G.js";import{Ca as i,H as a,Vn as o,wi as ee}from"./client-CkJPsoi-.js";import{n as te}from"./AuthContext-DdeYiTo7.js";import{P as s,bt as c,ft as ne,ht as re,vt as l,xt as u}from"./index-20XTCHW-.js";import{t as d}from"./PageHeader-CJqkZ5dE.js";import{t as f}from"./ErrorModal-DQRihwo7.js";import{t as p}from"./ActionMenu-BfaDLlea.js";import{t as m}from"./StatusBadge-s_6qr6NI.js";import{t as h}from"./JsonViewer-ClUapg1E.js";import{c as ie}from"./duplicates-Bzewu3fl.js";import{n as ae}from"./scoping-CZkYCmjW.js";import{a as g,o as _}from"./configuration-BvcQqnam.js";var v=t(r(),1),y=e(),oe=_,se=g,b=`Unique name Armada looks the template up by, in dotted form, for example persona.reviewer or mission.rules.custom. A persona or setting must name it for it to be used. Required.`,x=`Group the template belongs to, for example mission, persona, commit or landing. Pick a suggestion or type your own; it is used for filtering. Required.`,S=`Optional note on what this template is for, shown in the template list. It is not sent to the captain.`,C=`The prompt text. Placeholders in braces, such as {MissionTitle}, are filled in when the prompt is built; click one on the right to insert it. Required.`;function w(){let{t:e,formatDateTime:t}=n(),{isAdmin:r,isTenantAdmin:g,user:_}=te(),w={isAdmin:r,isTenantAdmin:g,tenantId:_?.user?.tenantId,userId:_?.user?.id},{name:T}=u(),E=c(),D=(0,v.useRef)(null),O=!T,[k,A]=(0,v.useState)(null),j=O?!0:!k||ae(w,k),[ce,M]=(0,v.useState)(!0),[N,P]=(0,v.useState)(``),[F,I]=(0,v.useState)(!1),{pushToast:L}=re(),[R,z]=(0,v.useState)(``),[B,V]=(0,v.useState)(`mission`),[H,U]=(0,v.useState)(``),[W,G]=(0,v.useState)(``),[K,q]=(0,v.useState)(!1),[J,Y]=(0,v.useState)({open:!1,title:``,data:null}),[X,Z]=(0,v.useState)({open:!1,title:``,message:``,onConfirm:()=>{}}),Q=(0,v.useCallback)(async()=>{if(O){A(null),z(``),V(`mission`),U(``),G(``),q(!1),P(``),M(!1);return}if(T)try{M(!0);let e=await o(T);A(e),z(e.name),V(e.category),U(e.content),G(e.description??``),q(!1),P(``)}catch(t){A(null),P(t instanceof Error?t.message:e(`Failed to load prompt template.`))}finally{M(!1)}},[O,T,e]);(0,v.useEffect)(()=>{Q()},[Q]);function le(e){z(e),q(!0)}function ue(e){V(e),q(!0)}function de(e){U(e),q(!0)}function fe(e){G(e),q(!0)}async function pe(){let t=R.trim(),n=B.trim(),r=W.trim();if(O){if(!t){P(e(`Template name is required.`));return}if(!n){P(e(`Template category is required.`));return}if(!H.trim()){P(e(`Template content is required.`));return}try{I(!0);let i=await a({name:t,category:n,content:H,description:r||void 0,active:!0});A(i),z(i.name),V(i.category),U(i.content),G(i.description??``),q(!1),P(``),L(`success`,e(`Template "{{name}}" created.`,{name:i.name})),E(`/prompt-templates/${encodeURIComponent(i.name)}`,{replace:!0})}catch(t){P(t instanceof Error?t.message:e(`Create failed.`))}finally{I(!1)}return}if(!(!T||!k))try{I(!0);let t=await i(T,{content:H,description:r||void 0});A(t),z(t.name),V(t.category),U(t.content),G(t.description??``),q(!1),P(``),L(`success`,e(`Template saved.`))}catch(t){P(t instanceof Error?t.message:e(`Save failed.`))}finally{I(!1)}}async function me(){if(k)try{I(!0);let t=await a(ie(k));L(`success`,e(`Template "{{name}}" duplicated.`,{name:t.name})),E(`/prompt-templates/${encodeURIComponent(t.name)}`)}catch(t){P(t instanceof Error?t.message:e(`Duplicate failed.`))}finally{I(!1)}}function $(){!k||!k.isBuiltIn||Z({open:!0,title:e(`Reset to Default`),message:e(`Reset "{{name}}" to its built-in default content? Your customizations will be lost.`,{name:k.name}),onConfirm:async()=>{Z(e=>({...e,open:!1}));try{let t=await ee(k.name);A(t),U(t.content),G(t.description??``),q(!1),L(`success`,e(`Template reset to default.`))}catch{P(e(`Reset failed.`))}}})}function he(e){let t=D.current;if(!t)return;let n=t.selectionStart,r=t.selectionEnd,i=H.substring(0,n)+e+H.substring(r);U(i),q(!0),requestAnimationFrame(()=>{t.focus(),t.selectionStart=n+e.length,t.selectionEnd=n+e.length})}return ce?(0,y.jsx)(`p`,{className:`text-dim`,children:e(`Loading...`)}):!O&&N&&!k?(0,y.jsx)(f,{error:N,onClose:()=>P(``)}):!O&&!k?(0,y.jsx)(`p`,{className:`text-dim`,children:e(`Template not found.`)}):(0,y.jsxs)(`div`,{children:[(0,y.jsx)(d,{breadcrumb:(0,y.jsxs)(y.Fragment,{children:[(0,y.jsx)(l,{to:`/prompt-templates`,children:e(`Prompt Templates`)}),` `,(0,y.jsx)(`span`,{className:`breadcrumb-sep`,children:`>`}),` `,(0,y.jsx)(`span`,{children:O?e(`Create`):R})]}),title:O?e(`Create Prompt Template`):R,actions:(0,y.jsx)(y.Fragment,{children:O?(0,y.jsx)(`span`,{className:`tag ${(B||`mission`).toLowerCase()}`,title:e(`Template category: the part of Armada that uses this template, such as persona, mission, commit or landing.`),children:e(B||`mission`)}):(0,y.jsxs)(y.Fragment,{children:[(0,y.jsx)(`span`,{className:`tag ${k.category.toLowerCase()}`,title:e(`Template category: the part of Armada that uses this template, such as persona, mission, commit or landing.`),children:e(k.category)}),k.isBuiltIn&&(0,y.jsx)(`span`,{className:`tag built-in`,title:e(`Ships with Armada. It cannot be deleted, and Reset to Default restores its original text.`),children:e(`Built-in`)}),(0,y.jsx)(p,{id:`template-${k.name}`,items:[{label:`Duplicate`,title:`Create a copy of this template under a new name, then open it. Armada only uses the copy where something refers to that new name.`,onClick:()=>void me()},{label:`View JSON`,title:`Show this template's full record as JSON, as the API returns it.`,onClick:()=>Y({open:!0,title:e(`Template: {{name}}`,{name:k.name}),data:k})},...k.isBuiltIn&&j?[{label:`Reset to Default`,danger:!0,title:`Replace this built-in template's text with the version that ships with Armada. Your edits are lost. You confirm first.`,onClick:$}]:[]]})]})})}),(0,y.jsx)(f,{error:N,onClose:()=>P(``)}),(0,y.jsx)(h,{open:J.open,title:J.title,data:J.data,onClose:()=>Y({open:!1,title:``,data:null})}),(0,y.jsx)(ne,{open:X.open,title:X.title,message:X.message,onConfirm:X.onConfirm,onCancel:()=>Z(e=>({...e,open:!1}))}),(0,y.jsx)(`style`,{children:`
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
      `}),(0,y.jsx)(`div`,{className:`detail-grid`,children:O?(0,y.jsxs)(y.Fragment,{children:[(0,y.jsxs)(`label`,{className:`detail-field template-meta-field`,title:e(b),children:[(0,y.jsx)(`span`,{className:`detail-label`,children:e(`Name`)}),(0,y.jsx)(`input`,{className:`template-description-input`,title:e(b),value:R,onChange:e=>le(e.target.value),placeholder:e(`mission.rules.custom`)})]}),(0,y.jsxs)(`label`,{className:`detail-field template-meta-field`,title:e(x),children:[(0,y.jsx)(`span`,{className:`detail-label`,children:e(`Category`)}),(0,y.jsx)(`input`,{className:`template-description-input`,title:e(x),list:`prompt-template-category-options`,value:B,onChange:e=>ue(e.target.value),placeholder:e(`mission`)}),(0,y.jsx)(`datalist`,{id:`prompt-template-category-options`,children:se.map(e=>(0,y.jsx)(`option`,{value:e},e))})]}),(0,y.jsxs)(`div`,{className:`detail-field`,children:[(0,y.jsx)(`span`,{className:`detail-label`,title:e(`Templates you create are custom: they can be edited or deleted, and have no built-in default to reset to.`),children:e(`Type`)}),(0,y.jsx)(`span`,{children:e(`Custom template`)})]})]}):(0,y.jsxs)(y.Fragment,{children:[(0,y.jsxs)(`div`,{className:`detail-field`,children:[(0,y.jsx)(`span`,{className:`detail-label`,title:e(`Unique template ID. Armada looks templates up by name, not ID.`),children:e(`ID`)}),(0,y.jsxs)(`span`,{className:`id-display`,children:[(0,y.jsx)(`span`,{className:`mono`,children:k.id}),(0,y.jsx)(s,{text:k.id})]})]}),(0,y.jsxs)(`div`,{className:`detail-field`,children:[(0,y.jsx)(`span`,{className:`detail-label`,title:e(`Marks the template as in use or retired. It is a label only: Armada still uses inactive templates.`),children:e(`Active`)}),(0,y.jsx)(m,{status:k.active===!1?`Inactive`:`Active`})]}),(0,y.jsxs)(`div`,{className:`detail-field`,children:[(0,y.jsx)(`span`,{className:`detail-label`,children:e(`Created`)}),(0,y.jsx)(`span`,{children:t(k.createdUtc)})]}),(0,y.jsxs)(`div`,{className:`detail-field`,children:[(0,y.jsx)(`span`,{className:`detail-label`,children:e(`Last Updated`)}),(0,y.jsx)(`span`,{children:k.lastUpdateUtc?t(k.lastUpdateUtc):`-`})]})]})}),(0,y.jsxs)(`div`,{className:`template-editor-layout`,children:[(0,y.jsxs)(`div`,{className:`template-editor-panel`,children:[(0,y.jsxs)(`label`,{style:{fontSize:`0.85em`,color:`var(--text-dim)`},title:e(S),children:[e(`Description`),(0,y.jsx)(`input`,{type:`text`,className:`template-description-input`,title:e(S),value:W,onChange:e=>fe(e.target.value),placeholder:e(`Template description...`)})]}),(0,y.jsxs)(`div`,{style:{display:`flex`,justifyContent:`space-between`,alignItems:`center`},children:[(0,y.jsxs)(`label`,{style:{fontSize:`0.85em`,color:`var(--text-dim)`,margin:0},title:e(C),children:[e(`Template Content`),K&&(0,y.jsx)(`span`,{className:`template-dirty-indicator`,title:e(`You have unsaved changes. Click Save to keep them.`)})]}),(0,y.jsxs)(`span`,{className:`template-char-count`,title:e(`Length of the template text before placeholders are filled in.`),children:[H.length,` `,e(`characters`)]})]}),(0,y.jsx)(`textarea`,{ref:D,className:`template-editor-textarea`,"aria-label":e(`Template Content`),title:e(C),value:H,onChange:e=>de(e.target.value),rows:30,spellCheck:!1}),(0,y.jsxs)(`div`,{className:`template-editor-actions`,children:[(0,y.jsx)(`button`,{className:`btn btn-primary`,onClick:pe,disabled:F||!K||!j||O&&(!R.trim()||!B.trim()||!H.trim()),title:e(O?`Create this template. Name, category and content are required.`:`Save the description and content. Prompts built after saving use the new text; running missions keep theirs.`),children:e(F?`Saving...`:`Save`)}),k?.isBuiltIn&&(0,y.jsx)(`button`,{className:`btn`,onClick:$,disabled:F,title:e(`Replace this built-in template's text with the version that ships with Armada. Your edits are lost. You confirm first.`),children:e(`Reset to Default`)}),(0,y.jsx)(`button`,{className:`btn`,onClick:()=>E(`/prompt-templates`),title:e(`Return to the template list. Unsaved changes are discarded.`),children:e(`Back`)})]})]}),(0,y.jsxs)(`div`,{className:`template-param-panel`,role:`region`,"aria-label":e(`Parameters`),tabIndex:0,children:[(0,y.jsx)(`h4`,{title:e(`Placeholders Armada fills in when it builds a prompt from this template. Not every placeholder applies to every category.`),children:e(`Parameters`)}),(0,y.jsx)(`p`,{style:{fontSize:`0.78em`,color:`var(--text-dim)`,margin:`0 0 0.75rem 0`},children:e(`Click a parameter to insert it at the cursor position.`)}),oe.map(t=>(0,y.jsxs)(`div`,{className:`template-param-group`,children:[(0,y.jsx)(`div`,{className:`template-param-group-label`,children:e(t.label)}),t.params.map(t=>(0,y.jsxs)(`div`,{className:`template-param-item`,onClick:()=>he(t.name),title:e(`Insert {{name}} -- {{description}}`,{name:t.name,description:e(t.description)}),children:[(0,y.jsx)(`span`,{className:`template-param-name`,children:t.name}),(0,y.jsx)(`span`,{className:`template-param-desc`,children:e(t.description)})]},t.name))]},t.label))]})]})]})}export{w as default};
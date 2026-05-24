const e=new Set(["idle","available"]);function t(n){return e.has((n??"").trim().toLowerCase())}function s(n){return n!=null&&n.supportsPlanningSessions?t(n.state):!1}export{s as c};

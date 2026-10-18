-- Aggregate-only observation report. Replace the two ISO UTC markers before execution.
-- Calls are events, not unique training examples. Captains and operator probes may mix.
-- A cache hit (typed_decision.cache_hit) reuses stored answers and is not a call; it is counted apart.
-- estimated_cost_usd is an estimate from the configured price table; calls_without_cost counts the
-- calls whose event names why no estimate exists (no price for the model, or an unavailable call).
with calls as (
 select *, payload::jsonb p, payload::jsonb->>'decision' decision_key from events
 where event_type in ('typed_decision.gated','typed_decision.shadow','typed_decision.unavailable','typed_decision.captain')
 and created_utc::timestamptz >= '__SINCE__'::timestamptz and created_utc::timestamptz < '__UNTIL__'::timestamptz
), reversals as (
 select payload::jsonb p from events where event_type='typed_decision.reversed'
 and created_utc::timestamptz >= '__SINCE__'::timestamptz and created_utc::timestamptz < '__UNTIL__'::timestamptz
 and coalesce(payload::jsonb->>'corrected_verdict','') <> 'test_call'
 and coalesce(payload::jsonb->>'reason','') not ilike '%smoke test%'
), cache_hits as (
 select payload::jsonb->>'decision' decision_key, count(*) hits from events where event_type='typed_decision.cache_hit'
 and created_utc::timestamptz >= '__SINCE__'::timestamptz and created_utc::timestamptz < '__UNTIL__'::timestamptz
 group by payload::jsonb->>'decision'
)
select json_build_object('kind','decision','decision',decision_key,
 'calls',count(*),'unique_states',count(distinct nullif(p->>'state_sha256','')),
 'mission_attributed_calls',count(*) filter(where nullif(mission_id,'') is not null),
 'first',min(created_utc::timestamptz),'last',max(created_utc::timestamptz),
 'applied',count(*) filter(where event_type='typed_decision.gated'),
 'unavailable',count(*) filter(where event_type='typed_decision.unavailable'),
 'latency_p50_ms',percentile_cont(0.5) within group(order by (p->>'latency_ms')::double precision),
 'latency_p95_ms',percentile_cont(0.95) within group(order by (p->>'latency_ms')::double precision),
 'mean_recorded_input_tokens',avg((p->>'input_tokens')::numeric),
 'mean_recorded_output_tokens',avg((p->>'output_tokens')::numeric),
 'batched_calls',count(*) filter(where (p->>'batch_size')::int>1),
 'estimated_cost_usd',sum((p->>'estimated_cost_usd')::numeric),
 'calls_without_cost',count(*) filter(where p->>'estimated_cost_usd' is null),
 'estimated_cost_per_applied_usd',sum((p->>'estimated_cost_usd')::numeric)/nullif(count(*) filter(where event_type='typed_decision.gated'),0),
 'price_table_versions',array_remove(array_agg(distinct p->>'price_table_version'),null),
 'cache_hits',coalesce((select h.hits from cache_hits h where h.decision_key=calls.decision_key),0),
 'real_reversals',(select count(*) from reversals r where r.p->>'decision'=calls.decision_key),
 'verified_accuracy',null,
 'recommendation','Keep current settings; insufficient independently verified outcomes for a threshold change')
from calls group by decision_key order by decision_key;

select json_build_object('kind','unavailable','decision',payload::jsonb->>'decision',
 'reason',payload::jsonb->>'unavailable_reason','count',count(*))
from events where event_type='typed_decision.unavailable'
 and created_utc::timestamptz >= '__SINCE__'::timestamptz and created_utc::timestamptz < '__UNTIL__'::timestamptz
 group by payload::jsonb->>'decision',payload::jsonb->>'unavailable_reason';

with rescue as (
 select v.* from voyages v where
 (v.title ~* '^Rescue( [0-9]+)?:' or exists (select 1 from missions m where m.voyage_id=v.id
 and coalesce(m.description,'') like '%<!-- ARMADA:AUTO-RESCUE -->%'))
 and v.created_utc::timestamptz >= '__SINCE__'::timestamptz and v.created_utc::timestamptz < '__UNTIL__'::timestamptz
)
select json_build_object('kind','rescue','total',count(*),
 'complete',count(*) filter(where status='Complete'),
 'complete_share',count(*) filter(where status='Complete')::numeric/nullif(count(*),0),
 'baseline_complete',78,'baseline_total',245) from rescue;

select json_build_object('kind','papercuts','events_in_window',count(*),'baseline_events',599)
from events where event_type='papercut'
 and created_utc::timestamptz >= '__SINCE__'::timestamptz and created_utc::timestamptz < '__UNTIL__'::timestamptz;

with latest as (
 select distinct on(entity_id) * from events where event_type='incident.snapshot'
 and created_utc::timestamptz < '__UNTIL__'::timestamptz order by entity_id,created_utc::timestamptz desc,id desc
), calls as (
 select * from events where event_type like 'typed_decision.%' and event_type not in ('typed_decision.reversed','typed_decision.cache_hit')
 and created_utc::timestamptz >= '__SINCE__'::timestamptz and created_utc::timestamptz < '__UNTIL__'::timestamptz
)
select json_build_object('kind','incident_evidence','calls_with_incident',count(distinct c.id),
 'calls_with_nonempty_root_cause',count(distinct c.id) filter(where nullif(i.payload::jsonb->>'RootCause','') is not null),
 'note','An incident root-cause string is an audit lead, not an independent label')
from calls c join latest i on i.mission_id=c.mission_id;

import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { decideAskProposal, listAskProposals, listAskWorkReports } from '../../api/client';
import type { AskActionProposal, AskWorkReport } from '../../types/models';

interface Props { threadId: string; busy: boolean; t: (value: string) => string; }

// The detail page of a tracked mission or voyage. A job has no page of its own.
function workPath(report: AskWorkReport): string | null {
  if (report.kind === 'Mission') return `/missions/${encodeURIComponent(report.id)}`;
  if (report.kind === 'Voyage') return `/voyages/${encodeURIComponent(report.id)}`;
  return null;
}

export default function AskActionCards({ threadId, busy, t }: Props) {
  const [proposals, setProposals] = useState<AskActionProposal[]>([]);
  const [work, setWork] = useState<AskWorkReport[]>([]);
  const [proposalError, setProposalError] = useState('');
  const [decisionNotice, setDecisionNotice] = useState('');
  const [workError, setWorkError] = useState('');
  const [pendingDecision, setPendingDecision] = useState('');
  const [refreshing, setRefreshing] = useState(false);
  const generation = useRef(0);
  const proposalRequest = useRef(0);
  const workRequest = useRef(0);

  const loadProposals = useCallback(async () => {
    const currentGeneration = generation.current;
    const request = ++proposalRequest.current;
    try {
      const result = await listAskProposals(threadId);
      if (generation.current !== currentGeneration || proposalRequest.current !== request) return;
      setProposals(result);
      setProposalError('');
    } catch (error) {
      if (generation.current === currentGeneration && proposalRequest.current === request)
        setProposalError(error instanceof Error ? error.message : t('Could not load action proposals.'));
    }
  }, [threadId, t]);

  const loadWork = useCallback(async (manual = false) => {
    const currentGeneration = generation.current;
    const request = ++workRequest.current;
    if (manual) setRefreshing(true);
    try {
      const result = await listAskWorkReports(threadId);
      if (generation.current !== currentGeneration || workRequest.current !== request) return;
      setWork(result);
      setWorkError('');
    } catch (error) {
      if (generation.current === currentGeneration && workRequest.current === request)
        setWorkError(error instanceof Error ? error.message : t('Could not load work reports.'));
    } finally {
      if (generation.current === currentGeneration && workRequest.current === request) setRefreshing(false);
    }
  }, [threadId, t]);

  useEffect(() => {
    generation.current += 1;
    setProposals([]); setWork([]); setProposalError(''); setDecisionNotice(''); setWorkError('');
    void loadProposals(); void loadWork();
    return () => { generation.current += 1; };
  }, [threadId, loadProposals, loadWork]);

  const unresolved = proposals.some((item) => item.state === 'Pending' || item.state === 'Executing' || item.state === 'Uncertain');
  useEffect(() => {
    if (!busy && !unresolved) return undefined;
    const id = window.setInterval(() => { void loadProposals(); }, 3000);
    return () => window.clearInterval(id);
  }, [busy, unresolved, loadProposals]);

  async function decide(proposal: AskActionProposal, decision: 'approve' | 'reject') {
    if (pendingDecision || proposal.state !== 'Pending') return;
    setPendingDecision(proposal.id);
    try {
      const result = await decideAskProposal(threadId, proposal.id, decision, proposal.fingerprint);
      if (result.proposal) setProposals((current) => current.map((item) => item.id === proposal.id ? result.proposal! : item));
      if (result.statusCode >= 400) {
        setProposalError(result.message || t('The action decision was refused.'));
        setDecisionNotice('');
      } else {
        setProposalError('');
        setDecisionNotice(result.message || '');
        if (decision === 'approve') void loadWork();
        void loadProposals();
      }
    } catch (error) {
      setProposalError(error instanceof Error ? error.message : t('The action decision failed.'));
    } finally { setPendingDecision(''); }
  }

  return <section className="ask-action-section" aria-label={t('Proposed actions and work')}>
    <div className="ask-action-heading"><h3>{t('Proposed actions')}</h3></div>
    {proposalError && <p className="ask-action-error" role="alert">{proposalError}</p>}
    {decisionNotice && <p className="text-dim" role="status">{decisionNotice}</p>}
    {proposals.map((proposal) => <article className="card ask-action-card" key={proposal.id}>
      <div className="ask-action-card-title"><strong>{proposal.summary || proposal.toolName}</strong><span className="badge">{proposal.state}</span></div>
      {proposal.summary && <p className="text-dim">{proposal.toolName}</p>}
      <pre className="ask-action-json">{proposal.argumentsJson}</pre>
      {proposal.state === 'Pending' && <div className="ask-action-buttons">
        <button className="btn btn-primary btn-sm" disabled={!!pendingDecision || Date.parse(proposal.expiresUtc) <= Date.now()} onClick={() => void decide(proposal, 'approve')}>{t('Approve')}</button>
        <button className="btn btn-sm" disabled={!!pendingDecision} onClick={() => void decide(proposal, 'reject')}>{t('Reject')}</button>
      </div>}
      {(proposal.state === 'Executing' || proposal.state === 'Uncertain') && <p className="text-dim" role="status">{t('The result is unknown. Armada will not retry this action. Check work reports before you decide what to do next.')}</p>}
      {proposal.state === 'Failed' && <p className="text-dim">{t('The tool recorded a failure. Work may still exist; check the work reports.')}</p>}
      {proposal.state === 'Pending' && Date.parse(proposal.expiresUtc) <= Date.now() && <p className="text-dim">{t('This proposal has expired and cannot be approved.')}</p>}
      {proposal.workReferences.filter((reference) => reference.kind === 'Job').map((reference) => <p className="text-dim" key={reference.id}>{t('Accepted job')}: {reference.id} · {proposal.state === 'Executing' || proposal.state === 'Uncertain' ? t('Outcome unknown; no automatic retry.') : proposal.state}</p>)}
      {proposal.outcome && <p className="ask-action-outcome">{proposal.outcome}</p>}
    </article>)}
    <div className="ask-action-heading"><h3>{t('Work reports')}</h3><button className="btn btn-sm" onClick={() => void loadWork(true)} disabled={refreshing}>{refreshing ? t('Refreshing…') : t('Refresh')}</button></div>
    {workError && <p className="ask-action-error" role="alert">{workError}</p>}
    {work.map((item) => <article className="card ask-action-card" key={`${item.kind}:${item.id}`}>
      <div className="ask-action-card-title"><strong>{workPath(item) ? <Link to={workPath(item)!}>{item.title}</Link> : item.title}</strong><span className="badge">{item.deliveryState}</span></div>
      <p>{item.summary}</p><p className="text-dim">{item.kind}: {item.status} · {item.producedWork ? t('Work produced') : t('Work not confirmed')} · {item.delivered ? t('Delivered') : t('Delivery not confirmed')}</p>
      {item.parentMissionId && <p className="text-dim">{t('Parent mission')}: <Link to={`/missions/${encodeURIComponent(item.parentMissionId)}`}>{item.parentMissionId}</Link></p>}
      {item.rescueMissionId && <p className="text-dim">{t('Rescue mission')}: <Link to={`/missions/${encodeURIComponent(item.rescueMissionId)}`}>{item.rescueMissionId}</Link></p>}
      {item.producedWork && !item.delivered && <p className="ask-action-warning" role="status">{t('Work was produced; delivery is not confirmed.')}</p>}
      {item.kind === 'Job' && ['lost', 'unknown', 'missing'].includes(item.status.toLowerCase()) && <p className="text-dim" role="status">{t('The job outcome is unknown. Armada will not retry it automatically.')}</p>}
    </article>)}
  </section>;
}

import { lazy, Suspense } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider } from './context/AuthContext';
import { LocaleProvider } from './context/LocaleContext';
import { ThemeProvider } from './context/ThemeContext';
import { WebSocketProvider } from './context/WebSocketContext';
import { NotificationProvider } from './context/NotificationContext';
import ProtectedRoute from './components/ProtectedRoute';
import Layout from './components/Layout';
import './App.css';

const Dashboard = lazy(() => import('./pages/Dashboard'));
const Fleets = lazy(() => import('./pages/Fleets'));
const Vessels = lazy(() => import('./pages/Vessels'));
const Captains = lazy(() => import('./pages/Captains'));
const Missions = lazy(() => import('./pages/Missions'));
const Voyages = lazy(() => import('./pages/Voyages'));
const Events = lazy(() => import('./pages/Events'));
const MergeQueue = lazy(() => import('./pages/MergeQueue'));
const Docks = lazy(() => import('./pages/Docks'));
const Doctor = lazy(() => import('./pages/Doctor'));
const Tenants = lazy(() => import('./pages/admin/Tenants'));
const Users = lazy(() => import('./pages/admin/Users'));
const Credentials = lazy(() => import('./pages/admin/Credentials'));
const Dispatch = lazy(() => import('./pages/Dispatch'));
const Planning = lazy(() => import('./pages/Planning'));
const Objectives = lazy(() => import('./pages/Objectives'));
const ObjectiveDetail = lazy(() => import('./pages/ObjectiveDetail'));
const Signals = lazy(() => import('./pages/Signals'));
const Notifications = lazy(() => import('./pages/Notifications'));
const Server = lazy(() => import('./pages/Server'));
const FleetDetail = lazy(() => import('./pages/FleetDetail'));
const VesselDetail = lazy(() => import('./pages/VesselDetail'));
const VesselOnboarding = lazy(() => import('./pages/VesselOnboarding'));
const CaptainDetail = lazy(() => import('./pages/CaptainDetail'));
const MissionDetail = lazy(() => import('./pages/MissionDetail'));
const VoyageDetail = lazy(() => import('./pages/VoyageDetail'));
const VoyageCreate = lazy(() => import('./pages/VoyageCreate'));
const SignalDetail = lazy(() => import('./pages/SignalDetail'));
const EventDetail = lazy(() => import('./pages/EventDetail'));
const DockDetail = lazy(() => import('./pages/DockDetail'));
const MergeQueueDetail = lazy(() => import('./pages/MergeQueueDetail'));
const Personas = lazy(() => import('./pages/Personas'));
const PersonaDetail = lazy(() => import('./pages/PersonaDetail'));
const Pipelines = lazy(() => import('./pages/Pipelines'));
const PipelineDetail = lazy(() => import('./pages/PipelineDetail'));
const PromptTemplates = lazy(() => import('./pages/PromptTemplates'));
const PromptTemplateDetail = lazy(() => import('./pages/PromptTemplateDetail'));
const Playbooks = lazy(() => import('./pages/Playbooks'));
const PlaybookDetail = lazy(() => import('./pages/PlaybookDetail'));
const WorkflowProfiles = lazy(() => import('./pages/WorkflowProfiles'));
const WorkflowProfileDetail = lazy(() => import('./pages/WorkflowProfileDetail'));
const CheckRuns = lazy(() => import('./pages/CheckRuns'));
const CheckRunDetail = lazy(() => import('./pages/CheckRunDetail'));
const Environments = lazy(() => import('./pages/Environments'));
const EnvironmentDetail = lazy(() => import('./pages/EnvironmentDetail'));
const Deployments = lazy(() => import('./pages/Deployments'));
const DeploymentDetail = lazy(() => import('./pages/DeploymentDetail'));
const Releases = lazy(() => import('./pages/Releases'));
const ReleaseDetail = lazy(() => import('./pages/ReleaseDetail'));
const Incidents = lazy(() => import('./pages/Incidents'));
const IncidentDetail = lazy(() => import('./pages/IncidentDetail'));
const Runbooks = lazy(() => import('./pages/Runbooks'));
const RunbookDetail = lazy(() => import('./pages/RunbookDetail'));
const Workspace = lazy(() => import('./pages/Workspace'));
const RequestHistory = lazy(() => import('./pages/RequestHistory'));
const ApiExplorer = lazy(() => import('./pages/ApiExplorer'));
const History = lazy(() => import('./pages/History'));

function RouteFallback() {
  return (
    <div style={{ padding: '2rem 2.5rem' }}>
      <p className="text-dim">Loading page...</p>
    </div>
  );
}

export default function App() {
  return (
    <LocaleProvider>
      <ThemeProvider>
        <AuthProvider>
          <WebSocketProvider>
            <NotificationProvider>
              <BrowserRouter basename="/dashboard">
                <Suspense fallback={<RouteFallback />}>
                  <Routes>
                    <Route element={<ProtectedRoute><Layout /></ProtectedRoute>}>
                      <Route index element={<Navigate to="/dashboard" replace />} />

                      <Route path="dashboard" element={<Dashboard />} />
                      <Route path="planning" element={<Planning />} />
                      <Route path="planning/:id" element={<Planning />} />
                      <Route path="dispatch" element={<Dispatch />} />
                      <Route path="backlog" element={<Objectives />} />
                      <Route path="backlog/:id" element={<ObjectiveDetail />} />
                      <Route path="objectives" element={<Objectives />} />
                      <Route path="objectives/:id" element={<ObjectiveDetail />} />

                      <Route path="fleets" element={<Fleets />} />
                      <Route path="fleets/:id" element={<FleetDetail />} />

                      <Route path="vessels" element={<Vessels />} />
                      <Route path="vessels/:id" element={<VesselDetail />} />
                      <Route path="vessels/:id/onboarding" element={<VesselOnboarding />} />
                      <Route path="workspace" element={<Workspace />} />
                      <Route path="workspace/:vesselId" element={<Workspace />} />
                      <Route path="workspace/:vesselId/:panel" element={<Workspace />} />

                      <Route path="captains" element={<Captains />} />
                      <Route path="captains/:id" element={<CaptainDetail />} />

                      <Route path="missions" element={<Missions />} />
                      <Route path="missions/:id" element={<MissionDetail />} />

                      <Route path="voyages" element={<Voyages />} />
                      <Route path="voyages/create" element={<VoyageCreate />} />
                      <Route path="voyages/:id" element={<VoyageDetail />} />

                      <Route path="signals" element={<Signals />} />
                      <Route path="history" element={<History />} />
                      <Route path="signals/:id" element={<SignalDetail />} />

                      <Route path="events" element={<Events />} />
                      <Route path="events/:id" element={<EventDetail />} />

                      <Route path="docks" element={<Docks />} />
                      <Route path="docks/:id" element={<DockDetail />} />

                      <Route path="merge-queue" element={<MergeQueue />} />
                      <Route path="merge-queue/:id" element={<MergeQueueDetail />} />

                      <Route path="personas" element={<Personas />} />
                      <Route path="personas/:name" element={<PersonaDetail />} />
                      <Route path="pipelines" element={<Pipelines />} />
                      <Route path="pipelines/:name" element={<PipelineDetail />} />
                      <Route path="prompt-templates" element={<PromptTemplates />} />
                      <Route path="prompt-templates/create" element={<PromptTemplateDetail />} />
                      <Route path="prompt-templates/:name" element={<PromptTemplateDetail />} />
                      <Route path="playbooks" element={<Playbooks />} />
                      <Route path="playbooks/:id" element={<PlaybookDetail />} />
                      <Route path="workflow-profiles" element={<WorkflowProfiles />} />
                      <Route path="workflow-profiles/:id" element={<WorkflowProfileDetail />} />
                      <Route path="checks" element={<CheckRuns />} />
                      <Route path="checks/:id" element={<CheckRunDetail />} />
                      <Route path="environments" element={<Environments />} />
                      <Route path="environments/:id" element={<EnvironmentDetail />} />
                      <Route path="deployments" element={<Deployments />} />
                      <Route path="deployments/:id" element={<DeploymentDetail />} />
                      <Route path="releases" element={<Releases />} />
                      <Route path="releases/new" element={<ReleaseDetail />} />
                      <Route path="releases/:id" element={<ReleaseDetail />} />
                      <Route path="incidents" element={<Incidents />} />
                      <Route path="incidents/:id" element={<IncidentDetail />} />
                      <Route path="runbooks" element={<Runbooks />} />
                      <Route path="runbooks/:id" element={<RunbookDetail />} />
                      <Route path="requests" element={<RequestHistory />} />
                      <Route path="requests/:id" element={<RequestHistory />} />
                      <Route path="api-explorer" element={<ApiExplorer />} />
                      <Route path="api-explorer/:operationId" element={<ApiExplorer />} />

                      <Route path="notifications" element={<Notifications />} />

                      <Route path="admin/tenants" element={<ProtectedRoute><Tenants /></ProtectedRoute>} />
                      <Route path="admin/users" element={<ProtectedRoute><Users /></ProtectedRoute>} />
                      <Route path="admin/credentials" element={<ProtectedRoute><Credentials /></ProtectedRoute>} />

                      <Route path="server" element={<Server />} />
                      <Route path="doctor" element={<Doctor />} />
                      <Route path="settings" element={<Navigate to="/server" replace />} />
                    </Route>
                  </Routes>
                </Suspense>
              </BrowserRouter>
            </NotificationProvider>
          </WebSocketProvider>
        </AuthProvider>
      </ThemeProvider>
    </LocaleProvider>
  );
}

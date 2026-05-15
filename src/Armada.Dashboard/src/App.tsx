import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider } from './context/AuthContext';
import { LocaleProvider } from './context/LocaleContext';
import { ThemeProvider } from './context/ThemeContext';
import { WebSocketProvider } from './context/WebSocketContext';
import { NotificationProvider } from './context/NotificationContext';
import ProtectedRoute from './components/ProtectedRoute';
import Layout from './components/Layout';

// Lazy-load pages — other agents own these files.
// We import them directly since they already exist as simple components.
import Dashboard from './pages/Dashboard';
import Fleets from './pages/Fleets';
import Vessels from './pages/Vessels';
import Captains from './pages/Captains';
import Missions from './pages/Missions';
import Voyages from './pages/Voyages';
import Events from './pages/Events';
import MergeQueue from './pages/MergeQueue';
import Docks from './pages/Docks';
import Doctor from './pages/Doctor';
import Tenants from './pages/admin/Tenants';
import Users from './pages/admin/Users';
import Credentials from './pages/admin/Credentials';
import './App.css';

import Dispatch from './pages/Dispatch';
import Planning from './pages/Planning';
import Objectives from './pages/Objectives';
import ObjectiveDetail from './pages/ObjectiveDetail';
import Signals from './pages/Signals';
import Notifications from './pages/Notifications';
import Server from './pages/Server';
import FleetDetail from './pages/FleetDetail';
import VesselDetail from './pages/VesselDetail';
import VesselOnboarding from './pages/VesselOnboarding';
import CaptainDetail from './pages/CaptainDetail';
import MissionDetail from './pages/MissionDetail';
import VoyageDetail from './pages/VoyageDetail';
import VoyageCreate from './pages/VoyageCreate';
import SignalDetail from './pages/SignalDetail';
import EventDetail from './pages/EventDetail';
import DockDetail from './pages/DockDetail';
import MergeQueueDetail from './pages/MergeQueueDetail';
import Personas from './pages/Personas';
import PersonaDetail from './pages/PersonaDetail';
import Pipelines from './pages/Pipelines';
import PipelineDetail from './pages/PipelineDetail';
import PromptTemplates from './pages/PromptTemplates';
import PromptTemplateDetail from './pages/PromptTemplateDetail';
import Playbooks from './pages/Playbooks';
import PlaybookDetail from './pages/PlaybookDetail';
import WorkflowProfiles from './pages/WorkflowProfiles';
import WorkflowProfileDetail from './pages/WorkflowProfileDetail';
import CheckRuns from './pages/CheckRuns';
import CheckRunDetail from './pages/CheckRunDetail';
import Environments from './pages/Environments';
import EnvironmentDetail from './pages/EnvironmentDetail';
import Deployments from './pages/Deployments';
import DeploymentDetail from './pages/DeploymentDetail';
import Releases from './pages/Releases';
import ReleaseDetail from './pages/ReleaseDetail';
import Incidents from './pages/Incidents';
import IncidentDetail from './pages/IncidentDetail';
import Runbooks from './pages/Runbooks';
import RunbookDetail from './pages/RunbookDetail';
import Workspace from './pages/Workspace';
import RequestHistory from './pages/RequestHistory';
import ApiExplorer from './pages/ApiExplorer';
import History from './pages/History';

export default function App() {
  return (
    <LocaleProvider>
      <ThemeProvider>
        <AuthProvider>
          <WebSocketProvider>
            <NotificationProvider>
            <BrowserRouter basename="/dashboard">
              <Routes>
                <Route element={<ProtectedRoute><Layout /></ProtectedRoute>}>
                  {/* Default route redirects to dashboard */}
                  <Route index element={<Navigate to="/dashboard" replace />} />

                  {/* Operations */}
                  <Route path="dashboard" element={<Dashboard />} />
                  <Route path="planning" element={<Planning />} />
                  <Route path="planning/:id" element={<Planning />} />
                  <Route path="dispatch" element={<Dispatch />} />
                  <Route path="backlog" element={<Objectives />} />
                  <Route path="backlog/:id" element={<ObjectiveDetail />} />
                  <Route path="objectives" element={<Objectives />} />
                  <Route path="objectives/:id" element={<ObjectiveDetail />} />

                  {/* Entities - List and Detail */}
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

                  {/* System */}
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

                  {/* Administration */}
                  <Route path="admin/tenants" element={<ProtectedRoute><Tenants /></ProtectedRoute>} />
                  <Route path="admin/users" element={<ProtectedRoute><Users /></ProtectedRoute>} />
                  <Route path="admin/credentials" element={<ProtectedRoute><Credentials /></ProtectedRoute>} />

                  {/* Tools */}
                  <Route path="server" element={<Server />} />
                  <Route path="doctor" element={<Doctor />} />
                  <Route path="settings" element={<Navigate to="/server" replace />} />
                </Route>
              </Routes>
            </BrowserRouter>
            </NotificationProvider>
          </WebSocketProvider>
        </AuthProvider>
      </ThemeProvider>
    </LocaleProvider>
  );
}

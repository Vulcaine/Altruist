import { CacheView } from '@/views/CacheView';
import { ConfigView } from '@/views/ConfigView';
import { ConnectionsView } from '@/views/ConnectionsView';
import { NetworkView } from '@/views/NetworkView';
import { PerformanceView } from '@/views/PerformanceView';
import { RoomsView } from '@/views/RoomsView';
import { ServerView } from '@/views/ServerView';
import { AutosaveView, GameplayView, PairingView } from '@/views/ServiceViews';
import { SummaryView } from '@/views/SummaryView';
import { EngineView } from '@/views/EngineView';
import { LabView } from '@/views/LabView';
import { VaultsView } from '@/views/VaultsView';
import { lazyView } from './lazy';
import type { ViewDef } from './views';

const WorldsView = lazyView(() => import('@/views/WorldsView').then((m) => m.WorldsView));

/** Every view, in navigation order within its group. */
export const VIEWS: readonly ViewDef[] = [
  { id: 'overview', label: 'Overview', icon: 'dashboard', group: 'Overview', component: SummaryView, feature: 'summary' },
  { id: 'config', label: 'Configuration', icon: 'settings-gear', group: 'Overview', component: ConfigView, feature: 'summary' },
  { id: 'server', label: 'Server & fleet', icon: 'server', group: 'Live', component: ServerView, feature: 'server' },
  { id: 'engine', label: 'Engine', icon: 'pulse', group: 'Live', component: EngineView, feature: 'engine' },
  { id: 'rooms', label: 'Rooms', icon: 'organization', group: 'Gameplay', component: RoomsView, feature: 'rooms' },
  { id: 'gameplay', label: 'AI & zones', icon: 'hubot', group: 'Gameplay', component: GameplayView, feature: 'gameplay' },
  { id: 'pairing', label: 'Pairing', icon: 'device-mobile', group: 'Gameplay', component: PairingView, feature: 'pairing' },
  { id: 'connections', label: 'Connections', icon: 'plug', group: 'Live', component: ConnectionsView, feature: 'sessions' },
  { id: 'worlds', label: 'Worlds', icon: 'globe', group: 'Live', component: WorldsView, feature: 'worlds' },
  { id: 'cache', label: 'Cache', icon: 'layers', group: 'Data', component: CacheView, feature: 'cache' },
  { id: 'autosave', label: 'Autosave', icon: 'save-all', group: 'Data', component: AutosaveView, feature: 'autosave' },
  { id: 'vaults', label: 'Vaults', icon: 'database', group: 'Data', component: VaultsView, feature: 'vaults' },
  { id: 'network', label: 'Network', icon: 'arrow-swap', group: 'Traffic', component: NetworkView, feature: 'network' },
  { id: 'performance', label: 'Performance', icon: 'graph', group: 'Traffic', component: PerformanceView, feature: 'performance' },
  { id: 'lab', label: 'API Lab', icon: 'beaker', group: 'Tools', component: LabView, feature: 'lab' },
];

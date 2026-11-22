import type { FunctionComponent } from 'preact';

/** What a view gets: the route's sub-path (e.g. the selected vault). */
export interface ViewProps {
  readonly rest: readonly string[];
}

/** One screen of the dashboard and where it sits in the navigation. */
export interface ViewDef {
  readonly id: string;
  readonly label: string;
  readonly icon: string;
  readonly group: 'Overview' | 'Live' | 'Gameplay' | 'Data' | 'Traffic' | 'Tools';
  readonly component: FunctionComponent<ViewProps>;
  /** The server feature the view needs (from the summary's feature list); always shown when absent. */
  readonly feature?: string;
}

export const GROUPS: readonly ViewDef['group'][] = ['Overview', 'Live', 'Gameplay', 'Data', 'Traffic', 'Tools'];

import { Routes } from '@angular/router';
import { CacheViewComponent } from './cache/cache-view.component';
import { SceneViewComponent } from './scene/scene-view/scene-view.component';
import { SessionComponent } from './session/session.component';
import { VaultViewComponent } from './vault/vault-view.component';

export const routes: Routes = [
  { path: 'scene', component: SceneViewComponent },
  { path: 'sessions', component: SessionComponent },
  { path: 'cache-view', component: CacheViewComponent },
  { path: 'vault-view', component: VaultViewComponent },
  { path: '', redirectTo: 'scene', pathMatch: 'full' },
  { path: '**', redirectTo: 'scene' },
];

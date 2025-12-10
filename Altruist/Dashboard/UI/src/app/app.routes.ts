import { Routes } from '@angular/router';
import { CacheViewComponent } from './cache/cache-view.component';
import { SceneViewComponent } from './scene/scene-view/scene-view.component';
import { SessionComponent } from './session/session.component';

export const routes: Routes = [
  { path: 'scene', component: SceneViewComponent },
  { path: 'sessions', component: SessionComponent },
  { path: 'cache', component: CacheViewComponent },
  { path: '', redirectTo: 'scene', pathMatch: 'full' },
  { path: '**', redirectTo: 'scene' },
];

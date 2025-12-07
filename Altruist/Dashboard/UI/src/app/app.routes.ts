import { Routes } from '@angular/router';
import { SceneViewComponent } from './scene/scene-view/scene-view.component';

export const routes: Routes = [
  {
    path: 'scene',
    component: SceneViewComponent,
  },
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'scene',
  },
  {
    path: '**',
    redirectTo: 'scene',
  },
];

import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { SceneViewComponent } from './scene/scene-view/scene-view.component';

const routes: Routes = [
  { path: 'scene', component: SceneViewComponent },
  { path: '', redirectTo: 'scene', pathMatch: 'full' },
  { path: '**', redirectTo: 'scene' },
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule],
})
export class AppRoutingModule {}

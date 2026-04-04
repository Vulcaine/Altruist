import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { RouterModule } from '@angular/router';

import { SceneViewComponent } from './scene-view/scene-view.component';

@NgModule({
  imports: [SceneViewComponent, CommonModule, RouterModule],
  exports: [SceneViewComponent],
})
export class SceneModule {}

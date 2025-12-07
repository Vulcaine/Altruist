import { Component } from '@angular/core';
import { GlobeSceneComponent } from '../globe-scene/globe-scene.component';

@Component({
  selector: 'app-scene-view',
  imports: [GlobeSceneComponent],
  templateUrl: './scene-view.component.html',
  styleUrl: './scene-view.component.scss',
})
export class SceneViewComponent {}

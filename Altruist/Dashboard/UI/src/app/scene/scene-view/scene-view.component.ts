import { Component } from '@angular/core';
import { GlobeSceneComponent } from '../globe-scene/globe-scene.component';

@Component({
  selector: 'app-scene-view',
  standalone: true,
  imports: [GlobeSceneComponent],
  templateUrl: './scene-view.component.html',
  styleUrl: './scene-view.component.scss',
})
export class SceneViewComponent {
  // TODO: wire these up to real data fetching logic
  isLoading = true;
  hasData = false;

  setLoading(value: boolean) {
    this.isLoading = value;
  }

  setHasData(value: boolean) {
    this.hasData = value;
  }

  get statusHint(): string {
    if (this.isLoading) return 'Loading...';
    if (!this.hasData) return 'No data available';
    return '';
  }
}

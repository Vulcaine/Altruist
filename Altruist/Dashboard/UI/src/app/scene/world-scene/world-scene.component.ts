import { CommonModule } from '@angular/common';
import { Component, Input } from '@angular/core';
import { GlobeSceneComponent } from '../globe-scene/globe-scene.component';
import { WorldObjectDto, WorldSummary } from '../models/world.model';

@Component({
  selector: 'app-world-scene',
  standalone: true,
  imports: [CommonModule, GlobeSceneComponent],
  templateUrl: './world-scene.component.html',
  styleUrl: './world-scene.component.scss',
})
export class WorldSceneComponent {
  @Input() world: WorldSummary | null = null;
  @Input() objects: WorldObjectDto[] = [];
  @Input() isLoading = false;

  get hasWorld(): boolean {
    return !!this.world;
  }

  get objectCount(): number {
    return this.objects.length;
  }
}

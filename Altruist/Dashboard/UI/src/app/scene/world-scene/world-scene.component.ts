import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  NgZone,
  OnChanges,
  OnDestroy,
  Output,
  SimpleChanges,
  ViewChild,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { DashboardWorldObjectStatePacket } from './models/world-realtime.model';
import { WorldObjectDto, WorldSummary } from './models/world.model';
import { WorldInputController } from './world-input.controller';
import { CameraInfo, WorldRenderer } from './world-renderer';
import { WorldService } from './world-scene.service';

@Component({
  selector: 'app-world-scene',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './world-scene.component.html',
  styleUrl: './world-scene.component.scss',
})
export class WorldSceneComponent
  implements AfterViewInit, OnChanges, OnDestroy
{
  @Input() world: WorldSummary | null = null;
  @Input() objects: WorldObjectDto[] = [];
  @Input() isLoading = false;
  @Input() selectedObject: WorldObjectDto | null = null;

  /** Emitted whenever a live world state packet is applied. */
  @Output() lastUpdateChanged = new EventEmitter<Date>();

  /** Emitted every frame with camera position/rotation. */
  @Output() cameraChanged = new EventEmitter<CameraInfo>();

  @ViewChild('viewport')
  viewportRef?: ElementRef<HTMLDivElement>;

  private hasThreeInitialized = false;
  private viewInitialized = false;

  private worldSub?: Subscription;

  private readonly inputController = new WorldInputController();
  private readonly renderer: WorldRenderer;

  constructor(
    private readonly worldService: WorldService,
    private readonly zone: NgZone,
  ) {
    // ✅ Ensure RAF callback updates Angular template properly
    this.renderer = new WorldRenderer(this.inputController, (info) => {
      this.zone.run(() => {
        this.cameraChanged.emit(info);
      });
    });
  }

  ngAfterViewInit(): void {
    this.viewInitialized = true;
    this.tryInitThree();
    this.ensureLiveUpdates();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['world'] && this.world && this.viewInitialized) {
      this.tryInitThree();
      this.ensureLiveUpdates();
    }

    if (
      this.viewInitialized &&
      changes['objects'] &&
      !changes['objects'].firstChange
    ) {
      this.renderer.rebuildColliders(this.objects, this.selectedObject);
    }

    if (changes['selectedObject'] && this.selectedObject) {
      this.renderer.rebuildColliders(this.objects, this.selectedObject);
    }
  }

  ngOnDestroy(): void {
    if (this.worldSub) {
      this.worldSub.unsubscribe();
      this.worldSub = undefined;
    }
    this.worldService.disconnect();
    this.renderer.dispose();
  }

  // ────────────────────────────────────────────────────────────────
  // Live updates (websocket)
  // ────────────────────────────────────────────────────────────────

  private ensureLiveUpdates(): void {
    if (!this.world) return;
    if (this.worldSub) return;

    this.worldSub = this.worldService
      .connect(this.world.index)
      .subscribe((packet) => {
        this.applyWorldState(packet);
      });
  }

  private applyWorldState(packet: DashboardWorldObjectStatePacket): void {
    if (!this.world || packet.worldIndex !== this.world.index) {
      return;
    }

    const byId = new Map<string, WorldObjectDto>();
    for (const obj of this.objects) {
      byId.set(obj.instanceId, obj);
    }

    for (const state of packet.objects) {
      const obj = byId.get(state.id);
      if (!obj) continue;

      obj.transform.position.x = state.position.x;
      obj.transform.position.y = state.position.y;
      obj.transform.position.z = state.position.z;
    }

    this.renderer.rebuildColliders(this.objects, this.selectedObject);

    // Emit last update time to parent (prefer server timestamp if valid)
    let ts: Date;
    if (packet.timestampUtc) {
      const parsed = new Date(packet.timestampUtc);
      ts = isNaN(parsed.getTime()) ? new Date() : parsed;
    } else {
      ts = new Date();
    }

    this.lastUpdateChanged.emit(ts);
  }

  // ────────────────────────────────────────────────────────────────
  // Three.js initialization guard
  // ────────────────────────────────────────────────────────────────

  private tryInitThree(): void {
    if (this.hasThreeInitialized) return;
    if (!this.world) return;
    if (!this.viewportRef) return;

    const container = this.viewportRef.nativeElement;

    this.renderer.init(container);
    this.renderer.rebuildColliders(this.objects, this.selectedObject);
    this.renderer.startLoop();

    this.hasThreeInitialized = true;
  }
}

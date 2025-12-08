// src/app/world/world.service.ts

import { Injectable, NgZone } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { DashboardWorldObjectStatePacket } from './models/world-realtime.model';

@Injectable({
  providedIn: 'root',
})
export class WorldService {
  private socket: WebSocket | null = null;
  private state$ = new Subject<DashboardWorldObjectStatePacket>();
  private currentWorldIndex: number | null = null;

  constructor(private readonly zone: NgZone) {}

  /**
   * Connect to the dashboard websocket for the given world index.
   * Returns an observable of world-state packets.
   */
  connect(worldIndex: number): Observable<DashboardWorldObjectStatePacket> {
    // If already connected for this world, just return the observable.
    if (this.socket && this.currentWorldIndex === worldIndex) {
      return this.state$.asObservable();
    }

    // If connected to a different world, close previous socket.
    this.disconnect();

    this.currentWorldIndex = worldIndex;

    const url = `ws://localhost:8000/ws/dashboard`;
    const ws = new WebSocket(url);
    this.socket = ws;

    ws.onopen = () => {
      // No subscription message needed – server broadcasts to all "dashboard" routes.
      // If later you need per-world subscription, send a small JSON packet here.
      // Example:
      // ws.send(JSON.stringify({ type: 'subscribeWorld', worldIndex }));
    };

    ws.onmessage = (event: MessageEvent) => {
      // We assume JSON payload compatible with DashboardWorldObjectStatePacket
      this.zone.run(() => {
        try {
          const data = JSON.parse(event.data);

          if (
            data &&
            typeof data === 'object' &&
            'worldIndex' in data &&
            data.worldIndex === this.currentWorldIndex
          ) {
            this.state$.next(data as DashboardWorldObjectStatePacket);
          }
        } catch (err) {
          console.error('Failed to parse dashboard world packet', err);
        }
      });
    };

    ws.onerror = (event) => {
      console.error('Dashboard websocket error', event);
    };

    ws.onclose = () => {
      this.zone.run(() => {
        this.socket = null;
        this.currentWorldIndex = null;
      });
    };

    return this.state$.asObservable();
  }

  disconnect(): void {
    if (this.socket) {
      try {
        this.socket.close();
      } catch {}
      this.socket = null;
    }
    this.currentWorldIndex = null;
  }
}

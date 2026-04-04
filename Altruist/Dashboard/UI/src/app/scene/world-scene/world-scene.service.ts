// src/app/world/world.service.ts

import { Injectable, NgZone } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { DashboardWorldObjectStatePacket } from './models/world-realtime.model';

interface Envelope {
  type: string;
  header?: any;
  message?: unknown;
}

@Injectable({
  providedIn: 'root',
})
export class WorldService {
  private socket: WebSocket | null = null;
  private state$ = new Subject<DashboardWorldObjectStatePacket>();
  private currentWorldIndex: number | null = null;

  private shouldReconnect = false;
  private reconnectTimeoutId: number | null = null;

  constructor(private readonly zone: NgZone) {}

  /**
   * Connect to the dashboard websocket for the given world index.
   * Returns an observable of world-state packets.
   */
  connect(worldIndex: number): Observable<DashboardWorldObjectStatePacket> {
    // If already connected for this world, just return stream.
    if (this.socket && this.currentWorldIndex === worldIndex) {
      return this.state$.asObservable();
    }

    // Explicit connect means we *do* want auto reconnect.
    this.shouldReconnect = true;

    // Tear down any existing socket first.
    this.cleanupSocket();

    this.currentWorldIndex = worldIndex;
    console.log('Connecting to dashboard websocket for world', worldIndex);

    this.openSocket(worldIndex);

    return this.state$.asObservable();
  }

  private openSocket(worldIndex: number): void {
    const url = `ws://localhost:8000/ws/dashboard`;
    const ws = new WebSocket(url);
    ws.binaryType = 'blob';
    this.socket = ws;

    ws.onopen = () => {
      // Clear any pending reconnect timer once we’re successfully connected.
      if (this.reconnectTimeoutId !== null) {
        clearTimeout(this.reconnectTimeoutId);
        this.reconnectTimeoutId = null;
      }
      // If you ever need per-world subscribe, send here:
      // ws.send(JSON.stringify({ type: 'SubscribeDashboardWorld', worldIndex }));
    };

    ws.onmessage = (event: MessageEvent) => {
      this.zone.run(() => {
        this.handleMessage(event);
      });
    };

    ws.onerror = (event) => {
      console.error('Dashboard websocket error', event);
    };

    ws.onclose = () => {
      this.zone.run(() => {
        this.socket = null;

        // If the app still cares about this world, auto-reconnect.
        if (this.shouldReconnect && this.currentWorldIndex !== null) {
          if (this.reconnectTimeoutId !== null) {
            clearTimeout(this.reconnectTimeoutId);
          }
          this.reconnectTimeoutId = window.setTimeout(() => {
            console.log(
              'Dashboard websocket closed, reconnecting for world',
              this.currentWorldIndex
            );
            if (this.currentWorldIndex !== null && this.shouldReconnect) {
              this.openSocket(this.currentWorldIndex);
            }
          }, 2000); // simple 2s backoff
        } else {
          this.currentWorldIndex = null;
        }
      });
    };
  }

  private handleMessage(event: MessageEvent): void {
    if (typeof event.data === 'string') {
      this.parseAndDispatch(event.data);
    } else if (event.data instanceof Blob) {
      event.data
        .text()
        .then((text) => this.parseAndDispatch(text))
        .catch((err) => {
          console.error('Failed to read dashboard world blob', err);
        });
    } else {
      console.warn(
        'Unsupported WS message type',
        typeof event.data,
        event.data
      );
    }
  }

  private parseAndDispatch(raw: string): void {
    try {
      const envelope = JSON.parse(raw) as Envelope;

      if (!envelope || envelope.type !== 'DashboardWorldObjectStatePacket') {
        return;
      }

      const msg = envelope.message as
        | DashboardWorldObjectStatePacket
        | undefined;

      if (!msg) {
        console.warn(
          'DashboardWorldObjectStatePacket has empty/invalid message payload',
          envelope
        );
        return;
      }

      if (
        this.currentWorldIndex !== null &&
        msg.worldIndex === this.currentWorldIndex
      ) {
        this.state$.next(msg);
      }
    } catch (err) {
      console.error('Failed to parse dashboard world packet', err, raw);
    }
  }

  /**
   * Called by the app when the scene is destroyed / world view closed.
   * Stops auto-reconnect and closes the socket.
   */
  disconnect(): void {
    this.shouldReconnect = false;

    if (this.reconnectTimeoutId !== null) {
      clearTimeout(this.reconnectTimeoutId);
      this.reconnectTimeoutId = null;
    }

    this.cleanupSocket();
    this.currentWorldIndex = null;
  }

  private cleanupSocket(): void {
    if (this.socket) {
      try {
        this.socket.close();
      } catch {
        // ignore
      }
      this.socket = null;
    }
  }
}

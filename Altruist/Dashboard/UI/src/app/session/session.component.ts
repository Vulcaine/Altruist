import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, OnInit } from '@angular/core';
import { forkJoin } from 'rxjs';

interface ConnectionDto {
  connectionId: string;
  ipAddress?: string | null;
  roomId?: string | null;
}

interface RoomSessionDto {
  roomId: string;
  connectionCount: number;
  connections: ConnectionDto[];
}

@Component({
  selector: 'app-session',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './session.component.html',
  styleUrl: './session.component.scss',
})
export class SessionComponent implements OnInit {
  private readonly baseUrl = '/dashboard/v1/sessions';

  isLoading = false;
  isMutating = false;
  errorMessage = '';

  rooms: RoomSessionDto[] = [];
  selectedRoom: RoomSessionDto | null = null;

  allConnections: ConnectionDto[] = [];

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    this.loadSessions();
  }

  loadSessions(): void {
    this.isLoading = true;
    this.errorMessage = '';

    const rooms$ = this.http.get<RoomSessionDto[]>(this.baseUrl);
    const allConnections$ = this.http.get<ConnectionDto[]>(
      `${this.baseUrl}/connections`
    );

    forkJoin({ rooms: rooms$, allConnections: allConnections$ }).subscribe({
      next: ({ rooms, allConnections }) => {
        this.rooms = rooms;
        this.allConnections = allConnections;
        this.isLoading = false;

        // keep selection if still present
        if (this.selectedRoom) {
          const match = rooms.find(
            (r) => r.roomId === this.selectedRoom!.roomId
          );
          this.selectedRoom = match ?? null;
        }
      },
      error: (err) => {
        console.error('Failed to load sessions', err);
        this.errorMessage = 'Failed to load sessions.';
        this.isLoading = false;
      },
    });
  }

  onRefresh(): void {
    this.loadSessions();
  }

  onSelectRoom(room: RoomSessionDto): void {
    this.selectedRoom = room;
  }

  onDeleteRoom(room: RoomSessionDto): void {
    if (this.isMutating) return;

    const confirmed = confirm(
      `Delete room "${room.roomId}" and disconnect ${room.connectionCount} connection(s)?`
    );
    if (!confirmed) return;

    this.isMutating = true;
    this.errorMessage = '';

    this.http
      .delete(`${this.baseUrl}/rooms/${encodeURIComponent(room.roomId)}`)
      .subscribe({
        next: () => {
          this.isMutating = false;
          if (this.selectedRoom?.roomId === room.roomId) {
            this.selectedRoom = null;
          }
          this.loadSessions();
        },
        error: (err) => {
          console.error('Failed to delete room', err);
          this.errorMessage = 'Failed to delete room.';
          this.isMutating = false;
        },
      });
  }

  onRemoveConnection(room: RoomSessionDto, connection: ConnectionDto): void {
    if (this.isMutating) return;

    const confirmed = confirm(
      `Remove connection "${connection.connectionId}" from room "${room.roomId}"? This will disconnect the client.`
    );
    if (!confirmed) return;

    this.isMutating = true;
    this.errorMessage = '';

    this.http
      .delete(
        `${this.baseUrl}/rooms/${encodeURIComponent(
          room.roomId
        )}/connections/${encodeURIComponent(connection.connectionId)}`
      )
      .subscribe({
        next: () => {
          this.isMutating = false;
          this.loadSessions();
        },
        error: (err) => {
          console.error('Failed to remove connection', err);
          this.errorMessage = 'Failed to remove connection.';
          this.isMutating = false;
        },
      });
  }

  onCloseSession(connection: ConnectionDto): void {
    if (this.isMutating) return;

    const confirmed = confirm(
      `Close session "${connection.connectionId}" and disconnect the client?`
    );
    if (!confirmed) return;

    this.isMutating = true;
    this.errorMessage = '';

    this.http
      .delete(
        `${this.baseUrl}/connections/${encodeURIComponent(
          connection.connectionId
        )}`
      )
      .subscribe({
        next: () => {
          this.isMutating = false;
          this.loadSessions();
        },
        error: (err) => {
          console.error('Failed to close session', err);
          this.errorMessage = 'Failed to close session.';
          this.isMutating = false;
        },
      });
  }

  get hasRooms(): boolean {
    return this.rooms.length > 0;
  }

  get hasSelection(): boolean {
    return !!this.selectedRoom;
  }
}

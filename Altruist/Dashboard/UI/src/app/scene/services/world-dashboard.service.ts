import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  WorldObjectDto,
  WorldSummary,
} from '../world-scene/models/world.model';

@Injectable({
  providedIn: 'root',
})
export class WorldDashboardService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/dashboard/v1/worlds';

  getWorlds(): Observable<WorldSummary[]> {
    return this.http.get<WorldSummary[]>(this.baseUrl);
  }

  streamWorldObjects(worldIndex: number): Observable<WorldObjectDto> {
    const url = `${this.baseUrl}/${worldIndex}/objects/stream`;

    return new Observable<WorldObjectDto>((subscriber: any) => {
      let cancelled = false;

      fetch(url)
        .then((response) => {
          if (!response.ok) {
            throw new Error(`HTTP ${response.status} ${response.statusText}`);
          }

          if (!response.body) {
            throw new Error('Streaming not supported: response.body is null');
          }

          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';

          const pump = (): Promise<void> =>
            reader.read().then(({ value, done }) => {
              if (cancelled) {
                reader.cancel().catch(() => {});
                return;
              }

              if (done) {
                // Parse any trailing line
                const tail = buffer.trim();
                if (tail.length > 0) {
                  try {
                    const obj = JSON.parse(tail) as WorldObjectDto;
                    subscriber.next(obj);
                  } catch (err) {
                    console.error(
                      'Failed to parse final NDJSON line',
                      tail,
                      err
                    );
                  }
                }
                subscriber.complete();
                return;
              }

              buffer += decoder.decode(value, { stream: true });

              let newlineIndex: number;
              // Process complete lines
              while ((newlineIndex = buffer.indexOf('\n')) >= 0) {
                const line = buffer.slice(0, newlineIndex).trim();
                buffer = buffer.slice(newlineIndex + 1);

                if (!line) continue;

                try {
                  const obj = JSON.parse(line) as WorldObjectDto;
                  subscriber.next(obj);
                } catch (err) {
                  console.error('Failed to parse NDJSON line', line, err);
                }
              }

              return pump();
            });

          return pump();
        })
        .catch((err) => {
          if (!cancelled) {
            subscriber.error(err);
          }
        });

      // Teardown logic
      return () => {
        cancelled = true;
      };
    });
  }
}

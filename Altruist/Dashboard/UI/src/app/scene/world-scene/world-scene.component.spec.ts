import { ComponentFixture, TestBed } from '@angular/core/testing';

import { WorldScene } from './world-scene.component';

describe('WorldScene', () => {
  let component: WorldScene;
  let fixture: ComponentFixture<WorldScene>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WorldScene]
    })
    .compileComponents();

    fixture = TestBed.createComponent(WorldScene);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});

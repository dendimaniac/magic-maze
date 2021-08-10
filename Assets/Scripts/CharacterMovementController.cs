using System.Collections.Generic;
using LiftStudio.EventChannels;
using Photon.Pun;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LiftStudio
{
    public class CharacterMovementController : MonoBehaviourPun, IPunInstantiateMagicCallback, IPunObservable
    {
        [SerializeField] private LayerMask characterLayerMask;
        [SerializeField] private LayerMask wallLayerMask;
        [SerializeField] private float characterFloatHeight = 0.5f;
        [SerializeField] private Texture2D holdCursor;
        [SerializeField] private Vector2Int cursorOffset;
        [SerializeField] private float characterMoveSpeed;

        [SerializeField] private GameEndedEventChannel gameEndedEventChannel;
        
        public Dictionary<CharacterType, bool> CharactersMoving { get; } = new Dictionary<CharacterType, bool>
        {
            {CharacterType.Axe, false}, {CharacterType.Bow, false},
            {CharacterType.Potion, false}, {CharacterType.Sword, false}
        };

        public MovementCardSettings MovementCardSettings { get; private set; }

        private Camera _gameCamera;
        private Transform _tempCharacter;
        private TilePlacer _tilePlacer;
        private Game _gameHandler;
        private Timer _timer;
        
        private Vector3 _mouseStartPosition;
        private Character _selectedCharacter;
        private GridCell _startGridCell;
        private GridCell _targetGridCell;
        private Vector3 _additionalFloatPosition;

        private Plane _plane = new Plane(Vector3.up, Vector3.zero);
        
        private static GameSetup GameSetupInstance => GameSetup.Instance;

        private Vector3 SelectedCharacterPosition
        {
            get => _selectedCharacter.transform.position;
            set => _selectedCharacter.transform.position = value;
        }

        private readonly Dictionary<CharacterType, Vector3> _photonPositionDictionary = new Dictionary<CharacterType, Vector3>();
        private readonly List<GridCell> _allPossibleCells = new List<GridCell>();

        private void Awake()
        {
            _additionalFloatPosition = new Vector3(0, characterFloatHeight, 0);
            gameEndedEventChannel.GameEnded += OnGameEnded;
        }
        
        public void OnPhotonInstantiate(PhotonMessageInfo info)
        {
            var data = info.photonView.InstantiationData;
            var cardSetupIndex = (int) data[0];
            var cardIndex = (int) data[1];
            var setupData = GameSetupInstance.GetCharacterMovementControllerSetupData(cardSetupIndex, cardIndex);
            _gameCamera = setupData.GameCamera;
            MovementCardSettings = setupData.MovementCardSettings;
            _tempCharacter = setupData.TempCharacter;
            _tilePlacer = setupData.TilePlacer;
            _gameHandler = setupData.GameHandler;
            _timer = setupData.Timer;
        }

        private void OnEnable()
        {
            PhotonNetwork.AddCallbackTarget(this);
        }

        private void Update()
        {
            if (!photonView.IsMine) return;

            if (EventSystem.current.IsPointerOverGameObject()) return;

            if (Input.GetMouseButtonDown(0))
            {
                if (_selectedCharacter == null)
                {
                    HandleSelectingCharacter();
                }
            }
            else if (Input.GetMouseButtonUp(0))
            {
                if (_selectedCharacter != null && _targetGridCell != null)
                {
                    HandlePlacingSelectedCharacter();
                }
            }
        }

        private void LateUpdate()
        {
            if (!photonView.IsMine)
            {
                foreach (var positionInfo in _photonPositionDictionary)
                {
                    if (!CharactersMoving[positionInfo.Key]) continue;
                    
                    var targetCharacter = _gameHandler.CharacterFromTypeDictionary[positionInfo.Key];
                    targetCharacter.transform.position = Vector3.MoveTowards(targetCharacter.transform.position,
                        positionInfo.Value, characterMoveSpeed * Time.deltaTime);
                }
                return;
            }
            
            if (EventSystem.current.IsPointerOverGameObject()) return;

            if (!_selectedCharacter) return;

            if (!Input.GetMouseButton(0)) return;

            var ray = _gameCamera.ScreenPointToRay(Input.mousePosition);
            _plane.Raycast(ray, out var enter);
            var planeHitPoint = ray.GetPoint(enter);
            var mouseMoveDirection = planeHitPoint - _mouseStartPosition;
            var targetPosition = _startGridCell.CenterWorldPosition + mouseMoveDirection;

            foreach (var placedTile in _tilePlacer.AllPlacedTiles)
            {
                var targetGridCell = placedTile.Grid.GetGridCellObject(targetPosition);
                if (targetGridCell == null) continue;

                if (targetGridCell.CharacterOnTop && targetGridCell != _startGridCell) return;

                if (targetGridCell.Exits != null && !_gameHandler.HasCharactersBeenOnPickupCells) return;

                SelectedCharacterPosition = targetPosition + _additionalFloatPosition;
                _targetGridCell = targetGridCell;
                _tempCharacter.position = _targetGridCell.CenterWorldPosition;
                _tempCharacter.gameObject.SetActive(true);
            }
        }

        private void HandleSelectingCharacter()
        {
            var ray = _gameCamera.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out var characterHitInfo, Mathf.Infinity, characterLayerMask)) return;

            var selectedCharacter = characterHitInfo.transform.GetComponent<Character>();
            if (CharactersMoving[selectedCharacter.CharacterType]) return;
            
            _plane.Raycast(ray, out var enter);
            _mouseStartPosition = ray.GetPoint(enter);
            _selectedCharacter = selectedCharacter;
            CharactersMoving[selectedCharacter.CharacterType] = true;
            var boardTile = _gameHandler.CharacterOnTileDictionary[selectedCharacter];
            _startGridCell = boardTile.Grid.GetGridCellObject(SelectedCharacterPosition);
            GetAllPossibleTargetGridCells();
            SelectedCharacterPosition += _additionalFloatPosition;
            Cursor.SetCursor(holdCursor, cursorOffset, CursorMode.Auto);
            var content = new object[] {_selectedCharacter.CharacterType};
            photonView.RPC("SelectCharacterRPC", RpcTarget.Others, content);
        }

        private void HandlePlacingSelectedCharacter()
        {
            if (float.IsNegativeInfinity(_targetGridCell.CenterWorldPosition.x)) return;

            if (!_allPossibleCells.Contains(_targetGridCell))
            {
                MoveCharacterToTargetPosition(_startGridCell);
                return;
            }
            
            if (_targetGridCell.Exits != null &&
                _targetGridCell.Exits.Exists(exit => exit.targetCharacterType == _selectedCharacter.CharacterType))
            {
                TakeCharacterOutOfBoard(_selectedCharacter);
                return;
            }

            if (_targetGridCell.Pickup != null &&
                _targetGridCell.Pickup.TargetCharacterType == _selectedCharacter.CharacterType)
            {
                _gameHandler.NotifyCharacterPlacedOnPickupCell(_selectedCharacter.CharacterType, _tempCharacter);
            }
            else if (_targetGridCell.Hourglass is {isAvailable: true})
            {
                var content = new object[]
                {
                    _tilePlacer.AllPlacedTiles.IndexOf(_targetGridCell.Tile),
                    _targetGridCell.CenterWorldPosition, _timer.CurrentTime
                };
                photonView.RPC("FlipHourglassRPC", RpcTarget.Others, content);
                _targetGridCell.UseHourglass();
                _timer.FlipHourglassTimer();
            }
            
            MoveCharacterToTargetPosition(_targetGridCell);
        }

        private void MoveCharacterToTargetPosition(GridCell targetGridCell)
        {
            _allPossibleCells.Clear();
            var targetTileIndex = _tilePlacer.AllPlacedTiles.IndexOf(targetGridCell.Tile);
            var startTileIndex = _tilePlacer.AllPlacedTiles.IndexOf(_startGridCell.Tile);
            var content = new object[]
            {
                _selectedCharacter.CharacterType, targetTileIndex, targetGridCell.CenterWorldPosition, startTileIndex,
                _startGridCell.CenterWorldPosition
            };
            photonView.RPC("TryPlaceCharacterRPC", RpcTarget.MasterClient, content);
            Cursor.SetCursor(null, Vector2.zero, CursorMode.ForceSoftware);
        }

        private void TakeCharacterOutOfBoard(Character targetCharacter)
        {
            var eventContent = new object[] {targetCharacter.CharacterType, _startGridCell.CenterWorldPosition};
            _startGridCell.ClearCharacter();
            _allPossibleCells.Clear();
            _tempCharacter.gameObject.SetActive(false);
            _startGridCell = null;
            _selectedCharacter = null;
            _targetGridCell = null;
            Cursor.SetCursor(null, Vector2.zero, CursorMode.ForceSoftware);
            
            photonView.RPC("TakeCharacterOutOfBoardRPC", RpcTarget.All, eventContent);
        }
        
        private void GetAllPossibleTargetGridCells()
        {
            _allPossibleCells.Add(_startGridCell);
            var possibleMovementDirection = MovementCardSettings.GetAllPossibleMovementVector();
            foreach (var movementDirection in possibleMovementDirection)
            {
                var startGridCell = _startGridCell;
                var nextGridCellPosition = startGridCell.CenterWorldPosition + movementDirection;
                for (var placedTileIndex = 0; placedTileIndex < _tilePlacer.AllPlacedTiles.Count; placedTileIndex++)
                {
                    var placedTile = _tilePlacer.AllPlacedTiles[placedTileIndex];
                    if (MovementCardSettings.canUsePortal)
                    {
                        var portalGridCell =
                            placedTile.GetTargetCharacterPortalGridCell(_selectedCharacter.CharacterType);
                        if (portalGridCell != null && !_allPossibleCells.Contains(portalGridCell))
                        {
                            _allPossibleCells.Add(portalGridCell);
                        }
                    }

                    if (MovementCardSettings.canUseElevator)
                    {
                        if (_startGridCell.Elevator != null &&
                            placedTile == _gameHandler.CharacterOnTileDictionary[_selectedCharacter])
                        {
                            var otherEndElevatorGridCell = placedTile.GetOtherElevatorEndGridCell(_startGridCell);
                            if (otherEndElevatorGridCell != null &&
                                !_allPossibleCells.Contains(otherEndElevatorGridCell))
                            {
                                _allPossibleCells.Add(otherEndElevatorGridCell);
                            }
                        }
                    }

                    do
                    {
                        var nextGridCell = placedTile.Grid.GetGridCellObject(nextGridCellPosition);
                        if (nextGridCell == null) break;

                        if (!Physics.Raycast(startGridCell.CenterWorldPosition, movementDirection, TilePlacer.CellSize,
                            wallLayerMask) && nextGridCell.CharacterOnTop == null)
                        {
                            _allPossibleCells.Add(nextGridCell);
                            placedTileIndex = -1;
                            startGridCell = nextGridCell;
                            nextGridCellPosition = startGridCell.CenterWorldPosition + movementDirection;
                            continue;
                        }

                        break;
                    } while (true);
                }
            }

            Debug.Log($"Count: {_allPossibleCells.Count}");
        }
        
        [PunRPC]
        private void SelectCharacterRPC(CharacterType targetCharacterType)
        {
            if (_selectedCharacter && targetCharacterType == _selectedCharacter.CharacterType) return;

            var targetCharacter = _gameHandler.CharacterFromTypeDictionary[targetCharacterType];
            CharactersMoving[targetCharacterType] = true;
            var targetCharacterTransform = targetCharacter.transform;
            var selectedCharacterPosition = targetCharacterTransform.position;
            selectedCharacterPosition += _additionalFloatPosition;
            targetCharacterTransform.position = selectedCharacterPosition;
        }

        [PunRPC]
        private void TryPlaceCharacterRPC(object[] data)
        {
            var targetCharacterType = (CharacterType) data[0];
            if (!CharactersMoving[targetCharacterType]) return;
            
            var targetTileIndex = (int) data[1];
            var targetTilePosition = (Vector3) data[2];
            
            var targetCharacter = _gameHandler.CharacterFromTypeDictionary[targetCharacterType];
            var targetTile = _tilePlacer.AllPlacedTiles[targetTileIndex];
            var targetGridCell = targetTile.Grid.GetGridCellObject(targetTilePosition);
            if (targetGridCell.CharacterOnTop &&
                targetGridCell.CharacterOnTop != targetCharacter) return;

            photonView.RPC("ConfirmPlaceCharacterRPC", RpcTarget.All, data);
        }

        [PunRPC]
        private void ConfirmPlaceCharacterRPC(object[] data)
        {
            var targetCharacterType = (CharacterType) data[0];
            if (!CharactersMoving[targetCharacterType]) return;
            
            var targetCharacter = _gameHandler.CharacterFromTypeDictionary[targetCharacterType];

            var targetTile = _tilePlacer.AllPlacedTiles[(int) data[1]];
            var targetGridCell = targetTile.Grid.GetGridCellObject((Vector3) data[2]);
            var startTile = _tilePlacer.AllPlacedTiles[(int) data[3]];
            var startGridCell = startTile.Grid.GetGridCellObject((Vector3) data[4]);

            CharactersMoving[targetCharacterType] = false;
            startGridCell.ClearCharacter();
            targetGridCell.SetCharacter(targetCharacter);
            _gameHandler.CharacterOnTileDictionary[targetCharacter] = targetGridCell.Tile;
            targetCharacter.transform.position = targetGridCell.CenterWorldPosition;

            if (!_selectedCharacter || targetCharacterType != _selectedCharacter.CharacterType) return;

            _tempCharacter.gameObject.SetActive(false);
            _startGridCell = targetGridCell;
            _selectedCharacter = null;
            _targetGridCell = null;
        }
        
        [PunRPC]
        private void TakeCharacterOutOfBoardRPC(CharacterType targetCharacterType, Vector3 gridCellPosition)
        {
            var targetCharacter = _gameHandler.CharacterFromTypeDictionary[targetCharacterType];
            var startTargetCharacterTile = _gameHandler.CharacterOnTileDictionary[targetCharacter];
            var targetCharacterInitialGridCell =
                startTargetCharacterTile.Grid.GetGridCellObject(gridCellPosition);
            targetCharacterInitialGridCell.ClearCharacter();
            targetCharacter.transform.position = _gameHandler.OutOfBoardTransform.position;
            targetCharacter.ToggleColliderOff();
            _gameHandler.NotifyTakeCharacterOutOfBoard(targetCharacter);
        }

        [PunRPC]
        private void FlipHourglassRPC(int tileIndex, Vector3 gridCellPosition, float senderTime)
        {
            var targetTile = _tilePlacer.AllPlacedTiles[tileIndex];
            var targetGridCell = targetTile.Grid.GetGridCellObject(gridCellPosition);
            targetGridCell.UseHourglass();
            _timer.FlipHourglassTimer(senderTime);
        }

        private void OnGameEnded()
        {
            enabled = false;
        }
        
        public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
        {
            if (stream.IsWriting)
            {
                if (!_selectedCharacter) return;
                
                stream.SendNext(_selectedCharacter.CharacterType);
                stream.SendNext(_selectedCharacter.transform.position);
                return;
            }
            
            var targetCharacterType = (CharacterType) stream.ReceiveNext();
            var targetCharacterPosition = (Vector3) stream.ReceiveNext();
            if (!CharactersMoving[targetCharacterType]) return;

            _photonPositionDictionary[targetCharacterType] = targetCharacterPosition;
        }

        private void OnDisable()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
        }

        private void OnDestroy()
        {
            gameEndedEventChannel.GameEnded -= OnGameEnded;
        }
    }
}
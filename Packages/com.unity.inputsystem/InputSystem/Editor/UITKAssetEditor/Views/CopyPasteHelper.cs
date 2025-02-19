#if UNITY_EDITOR && UNITY_INPUT_SYSTEM_PROJECT_WIDE_ACTIONS
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;

namespace UnityEngine.InputSystem.Editor
{
    internal static class CopyPasteHelper
    {
        private const string k_CopyPasteMarker = "INPUTASSET ";
        private const string k_StartOfText = "\u0002";
        private const string k_EndOfTransmission = "\u0004";
        private const string k_BindingData = "bindingData";
        private const string k_EndOfBinding = "+++";
        private static readonly Dictionary<Type, string> k_TypeMarker = new Dictionary<Type, string>
        {
            {typeof(InputActionMap), "InputActionMap"},
            {typeof(InputAction), "InputAction"},
            {typeof(InputBinding), "InputBinding"},
        };

        private static SerializedProperty s_lastAddedElement;
        private static InputActionsEditorState s_State;
        private static bool s_lastClipboardActionWasCut = false;

        private static bool IsComposite(SerializedProperty property) => property.FindPropertyRelative("m_Flags").intValue == (int)InputBinding.Flags.Composite;
        private static bool IsPartOfComposite(SerializedProperty property) => property.FindPropertyRelative("m_Flags").intValue == (int)InputBinding.Flags.PartOfComposite;
        private static string PropertyName(SerializedProperty property) => property.FindPropertyRelative("m_Name").stringValue;

        #region Cut

        public static void CutActionMap(InputActionsEditorState state)
        {
            CopyActionMap(state);
            s_lastClipboardActionWasCut = true;
        }

        public static void Cut(InputActionsEditorState state)
        {
            Copy(state);
            s_lastClipboardActionWasCut = true;
        }

        #endregion

        #region Copy

        public static void CopyActionMap(InputActionsEditorState state)
        {
            var actionMap = Selectors.GetSelectedActionMap(state)?.wrappedProperty;
            var selectedObject = Selectors.GetSelectedActionMap(state)?.wrappedProperty;
            CopySelectedTreeViewItemsToClipboard(new List<SerializedProperty> {selectedObject}, typeof(InputActionMap), actionMap);
        }

        public static void Copy(InputActionsEditorState state)
        {
            var actionMap = Selectors.GetSelectedActionMap(state)?.wrappedProperty;
            var selectedObject = Selectors.GetSelectedAction(state)?.wrappedProperty;
            var type = typeof(InputAction);
            if (state.selectionType == SelectionType.Binding)
            {
                selectedObject = Selectors.GetSelectedBinding(state)?.wrappedProperty;
                type = typeof(InputBinding);
            }
            CopySelectedTreeViewItemsToClipboard(new List<SerializedProperty> {selectedObject}, type, actionMap);
        }

        private static void CopySelectedTreeViewItemsToClipboard(List<SerializedProperty> items, Type type, SerializedProperty actionMap = null)
        {
            var copyBuffer = new StringBuilder();
            CopyItems(items, copyBuffer, type, actionMap);
            EditorGUIUtility.systemCopyBuffer = copyBuffer.ToString();
            s_lastClipboardActionWasCut = false;
        }

        private static void CopyItems(List<SerializedProperty> items, StringBuilder buffer, Type type, SerializedProperty actionMap)
        {
            buffer.Append(k_CopyPasteMarker);
            buffer.Append(k_TypeMarker[type]);
            foreach (var item in items)
            {
                CopyItemData(item, buffer, type, actionMap);
                buffer.Append(k_EndOfTransmission);
            }
        }

        private static void CopyItemData(SerializedProperty item, StringBuilder buffer, Type type, SerializedProperty actionMap)
        {
            buffer.Append(k_StartOfText);
            buffer.Append(item.CopyToJson(true));
            if (type == typeof(InputAction))
                AppendBindingDataForAction(buffer, actionMap, item);
            if (type == typeof(InputBinding) && IsComposite(item))
                AppendBindingDataForComposite(buffer, actionMap, item);
        }

        private static void AppendBindingDataForAction(StringBuilder buffer, SerializedProperty actionMap, SerializedProperty item)
        {
            buffer.Append(k_BindingData);
            foreach (var binding in GetBindingsForActionInMap(actionMap, item))
            {
                buffer.Append(binding.CopyToJson(true));
                buffer.Append(k_EndOfBinding);
            }
        }

        private static void AppendBindingDataForComposite(StringBuilder buffer, SerializedProperty actionMap, SerializedProperty item)
        {
            var bindingsArray = actionMap.FindPropertyRelative(nameof(InputActionMap.m_Bindings));
            buffer.Append(k_BindingData);
            foreach (var binding in GetBindingsForComposite(bindingsArray, item.GetIndexOfArrayElement()))
            {
                buffer.Append(binding.CopyToJson(true));
                buffer.Append(k_EndOfBinding);
            }
        }

        private static IEnumerable<SerializedProperty> GetBindingsForActionInMap(SerializedProperty actionMap, SerializedProperty action)
        {
            var actionName = PropertyName(action);
            var bindingsArray = actionMap.FindPropertyRelative(nameof(InputActionMap.m_Bindings));
            var bindings = bindingsArray.Where(binding => binding.FindPropertyRelative("m_Action").stringValue.Equals(actionName));
            return bindings;
        }

        #endregion

        #region PasteChecks
        public static bool HasPastableClipboardData(Type selectedType)
        {
            var clipboard = EditorGUIUtility.systemCopyBuffer;
            if (clipboard.Length < k_CopyPasteMarker.Length)
                return false;
            var isInputAssetData = clipboard.StartsWith(k_CopyPasteMarker);
            return isInputAssetData && IsMatchingType(selectedType, GetCopiedClipboardType());
        }

        private static bool IsMatchingType(Type selectedType, Type copiedType)
        {
            if (selectedType == typeof(InputActionMap))
                return copiedType == typeof(InputActionMap) || copiedType == typeof(InputAction);
            if (selectedType == typeof(InputAction))
                return copiedType == typeof(InputAction) || copiedType == typeof(InputBinding);
            //bindings and composites
            return copiedType == typeof(InputBinding);
        }

        public static Type GetCopiedClipboardType()
        {
            if (!EditorGUIUtility.systemCopyBuffer.StartsWith(k_CopyPasteMarker))
                return null;
            foreach (var typePair in k_TypeMarker)
            {
                if (EditorGUIUtility.systemCopyBuffer.Substring(k_CopyPasteMarker.Length).StartsWith(typePair.Value))
                    return typePair.Key;
            }
            return null;
        }

        #endregion

        #region Paste

        public static SerializedProperty PasteActionMapsFromClipboard(InputActionsEditorState state)
        {
            s_lastAddedElement = null;
            var typeOfCopiedData = GetCopiedClipboardType();
            if (typeOfCopiedData != typeof(InputActionMap)) return null;
            s_State = state;
            var actionMapArray = state.serializedObject.FindProperty(nameof(InputActionAsset.m_ActionMaps));
            PasteData(EditorGUIUtility.systemCopyBuffer, new[] {state.selectedActionMapIndex}, actionMapArray);

            // Don't want to be able to paste repeatedly after a cut - ISX-1821
            if (s_lastAddedElement != null && s_lastClipboardActionWasCut)
                EditorGUIUtility.systemCopyBuffer = string.Empty;

            return s_lastAddedElement;
        }

        public static SerializedProperty PasteActionsOrBindingsFromClipboard(InputActionsEditorState state, bool addLast = false, int mapIndex = -1)
        {
            s_lastAddedElement = null;
            s_State = state;
            var typeOfCopiedData = GetCopiedClipboardType();
            if (typeOfCopiedData == typeof(InputAction))
                PasteActionsFromClipboard(state, addLast, mapIndex);
            if (typeOfCopiedData == typeof(InputBinding))
                PasteBindingsFromClipboard(state);

            // Don't want to be able to paste repeatedly after a cut - ISX-1821
            if (s_lastAddedElement != null && s_lastClipboardActionWasCut)
                EditorGUIUtility.systemCopyBuffer = string.Empty;

            return s_lastAddedElement;
        }

        private static void PasteActionsFromClipboard(InputActionsEditorState state, bool addLast, int mapIndex)
        {
            var actionMap = mapIndex >= 0 ? Selectors.GetActionMapAtIndex(state, mapIndex)?.wrappedProperty
                : Selectors.GetSelectedActionMap(state)?.wrappedProperty;
            var actionArray = actionMap?.FindPropertyRelative(nameof(InputActionMap.m_Actions));
            if (actionArray == null) return;
            var index = state.selectedActionIndex;
            if (addLast)
                index = actionArray.arraySize - 1;
            PasteData(EditorGUIUtility.systemCopyBuffer, new[] {index}, actionArray);
        }

        private static void PasteBindingsFromClipboard(InputActionsEditorState state)
        {
            var actionMap = Selectors.GetSelectedActionMap(state)?.wrappedProperty;
            var bindingsArray = actionMap?.FindPropertyRelative(nameof(InputActionMap.m_Bindings));
            var actions = actionMap?.FindPropertyRelative(nameof(InputActionMap.m_Actions));
            var index = state.selectionType == SelectionType.Action ? Selectors.GetBindingIndexBeforeAction(actions, state.selectedActionIndex, bindingsArray) : state.selectedBindingIndex;
            PasteData(EditorGUIUtility.systemCopyBuffer, new[] {index}, bindingsArray);
        }

        private static void PasteData(string copyBufferString, int[] indicesToInsert, SerializedProperty arrayToInsertInto)
        {
            if (!copyBufferString.StartsWith(k_CopyPasteMarker))
                return;
            PasteItems(copyBufferString, indicesToInsert, arrayToInsertInto);
        }

        private static void PasteItems(string copyBufferString, int[] indicesToInsert, SerializedProperty arrayToInsertInto)
        {
            // Split buffer into transmissions and then into transmission blocks
            int indexOffset = 0;
            foreach (var transmission in copyBufferString.Substring(k_CopyPasteMarker.Length + k_TypeMarker[GetCopiedClipboardType()].Length)
                     .Split(new[] {k_EndOfTransmission}, StringSplitOptions.RemoveEmptyEntries))
            {
                indexOffset += 1;
                foreach (var index in indicesToInsert)
                    PasteBlocks(transmission, index + indexOffset, arrayToInsertInto);
            }
        }

        private static void PasteBlocks(string transmission, int indexToInsert, SerializedProperty arrayToInsertInto)
        {
            var block = transmission.Substring(transmission.IndexOf(k_StartOfText, StringComparison.Ordinal) + 1);
            var copiedType = GetCopiedClipboardType();
            if (copiedType == typeof(InputActionMap))
                PasteElement(arrayToInsertInto, block, indexToInsert, out _);
            else if (copiedType == typeof(InputAction))
                PasteAction(arrayToInsertInto, block, indexToInsert);
            else
            {
                var actionName = Selectors.GetSelectedBinding(s_State)?.wrappedProperty.FindPropertyRelative("m_Action")
                    .stringValue;
                if (s_State.selectionType == SelectionType.Action)
                    actionName = PropertyName(Selectors.GetSelectedAction(s_State)?.wrappedProperty);
                PasteBindingOrComposite(arrayToInsertInto, block, indexToInsert, actionName);
            }
        }

        private static SerializedProperty PasteElement(SerializedProperty arrayProperty, string json, int index, out string oldId, string name = "newElement",  bool changeName = true, bool assignUniqueIDs = true)
        {
            var duplicatedProperty = AddElement(arrayProperty, name, index);
            duplicatedProperty.RestoreFromJson(json);
            oldId = duplicatedProperty.FindPropertyRelative("m_Id").stringValue;
            if (changeName)
                InputActionSerializationHelpers.EnsureUniqueName(duplicatedProperty);
            if (assignUniqueIDs)
                InputActionSerializationHelpers.AssignUniqueIDs(duplicatedProperty);
            s_lastAddedElement = duplicatedProperty;
            return duplicatedProperty;
        }

        private static void PasteAction(SerializedProperty arrayProperty, string jsonToInsert, int indexToInsert)
        {
            var json = jsonToInsert.Split(k_BindingData, StringSplitOptions.RemoveEmptyEntries);
            var bindingJsons = json[1].Split(k_EndOfBinding, StringSplitOptions.RemoveEmptyEntries);
            var property = PasteElement(arrayProperty, json[0], indexToInsert, out _, "");
            var newName = PropertyName(property);
            var newId = property.FindPropertyRelative("m_Id").stringValue;
            var actionMapTo = Selectors.GetActionMapForAction(s_State, newId);
            var bindingArrayToInsertTo = actionMapTo.FindPropertyRelative(nameof(InputActionMap.m_Bindings));
            var index = Selectors.GetBindingIndexBeforeAction(arrayProperty, indexToInsert, bindingArrayToInsertTo);
            foreach (var bindingJson in bindingJsons)
            {
                var newIndex = PasteBindingOrComposite(bindingArrayToInsertTo, bindingJson, index, newName, false);
                index = newIndex;
            }
            s_lastAddedElement = property;
        }

        private static int PasteBindingOrComposite(SerializedProperty arrayProperty, string json, int index, string actionName, bool createCompositeParts = true)
        {
            var pastePartOfComposite = IsPartOfComposite(json);
            if (index > 0)
            {
                var currentProperty = arrayProperty.GetArrayElementAtIndex(index - 1);
                var currentIsComposite = IsComposite(currentProperty) || IsPartOfComposite(currentProperty);
                if (pastePartOfComposite && !currentIsComposite) //prevent pasting part of composite into non-composite
                    return index;
            }
            index = pastePartOfComposite || s_State.selectionType == SelectionType.Action ? index : Selectors.GetSelectedBindingIndexAfterCompositeBindings(s_State) + 1;
            if (json.Contains(k_BindingData)) //copied data is composite with bindings - only true for directly copied composites, not for composites from copied actions
                return PasteCompositeFromJson(arrayProperty, json, index, actionName);
            var property = PasteElement(arrayProperty, json, index, out var oldId, "", false);
            if (IsComposite(property))
                return PasteComposite(arrayProperty, property, PropertyName(property), actionName, index, oldId, createCompositeParts); //Paste composites copied with actions
            property.FindPropertyRelative("m_Action").stringValue = actionName;
            return index + 1;
        }

        private static int PasteComposite(SerializedProperty bindingsArray, SerializedProperty duplicatedComposite, string name, string actionName, int index, string oldId, bool createCompositeParts)
        {
            duplicatedComposite.FindPropertyRelative("m_Name").stringValue = name;
            duplicatedComposite.FindPropertyRelative("m_Action").stringValue = actionName;
            if (createCompositeParts)
            {
                var composite = Selectors.GetBindingForId(s_State, oldId, out var bindingsFrom);
                var bindings = GetBindingsForComposite(bindingsFrom, composite.GetIndexOfArrayElement());
                PastePartsOfComposite(bindingsArray, bindings, ++index, actionName);
            }
            return index + 1;
        }

        private static int PastePartsOfComposite(SerializedProperty bindingsToInsertTo, List<SerializedProperty> bindingsOfComposite, int index, string actionName)
        {
            foreach (var binding in bindingsOfComposite)
            {
                var newBinding = DuplicateElement(bindingsToInsertTo, binding, PropertyName(binding), index++, false);
                newBinding.FindPropertyRelative("m_Action").stringValue = actionName;
            }

            return index;
        }

        private static int PasteCompositeFromJson(SerializedProperty arrayProperty, string json, int index, string actionName)
        {
            var jsons = json.Split(k_BindingData, StringSplitOptions.RemoveEmptyEntries);
            var property = PasteElement(arrayProperty, jsons[0], index, out _, "", false);
            var bindingJsons = jsons[1].Split(k_EndOfBinding, StringSplitOptions.RemoveEmptyEntries);
            property.FindPropertyRelative("m_Action").stringValue = actionName;
            foreach (var bindingJson in bindingJsons)
                PasteBindingOrComposite(arrayProperty, bindingJson, ++index, actionName, false);
            return index + 1;
        }

        private static bool IsPartOfComposite(string json)
        {
            if (!json.Contains("m_Flags") || json.Contains(k_BindingData))
                return false;
            var ob = JsonUtility.FromJson<InputBinding>(json);
            return ob.m_Flags == InputBinding.Flags.PartOfComposite;
        }

        private static SerializedProperty AddElement(SerializedProperty arrayProperty, string name, int index = -1)
        {
            var uniqueName = InputActionSerializationHelpers.FindUniqueName(arrayProperty, name);
            if (index < 0)
                index = arrayProperty.arraySize;

            arrayProperty.InsertArrayElementAtIndex(index);
            var elementProperty = arrayProperty.GetArrayElementAtIndex(index);
            elementProperty.ResetValuesToDefault();

            elementProperty.FindPropertyRelative("m_Name").stringValue = uniqueName;
            elementProperty.FindPropertyRelative("m_Id").stringValue = Guid.NewGuid().ToString();

            return elementProperty;
        }

        #endregion

        #region Duplicate
        public static void DuplicateAction(SerializedProperty arrayProperty, SerializedProperty toDuplicate, SerializedProperty actionMap, InputActionsEditorState state)
        {
            s_State = state;
            var buffer = new StringBuilder();
            buffer.Append(toDuplicate.CopyToJson(true));
            AppendBindingDataForAction(buffer, actionMap, toDuplicate);
            PasteAction(arrayProperty, buffer.ToString(), toDuplicate.GetIndexOfArrayElement() + 1);
        }

        public static int DuplicateBinding(SerializedProperty arrayProperty, SerializedProperty toDuplicate, string newActionName, int index)
        {
            if (IsComposite(toDuplicate))
                return DuplicateComposite(arrayProperty, toDuplicate, PropertyName(toDuplicate), newActionName, index, out _).GetIndexOfArrayElement();
            var binding = DuplicateElement(arrayProperty, toDuplicate, newActionName, index, false);
            binding.FindPropertyRelative("m_Action").stringValue = newActionName;
            return index;
        }

        private static SerializedProperty DuplicateComposite(SerializedProperty bindingsArray, SerializedProperty compositeToDuplicate, string name, string actionName, int index, out int newIndex, bool increaseIndex = true)
        {
            if (increaseIndex)
                index += InputActionSerializationHelpers.GetCompositePartCount(bindingsArray, compositeToDuplicate.GetIndexOfArrayElement());
            var newComposite = DuplicateElement(bindingsArray, compositeToDuplicate, name, index++, false);
            newComposite.FindPropertyRelative("m_Action").stringValue = actionName;
            var bindings = GetBindingsForComposite(bindingsArray, compositeToDuplicate.GetIndexOfArrayElement());
            newIndex = PastePartsOfComposite(bindingsArray, bindings, index, actionName);
            return newComposite;
        }

        public static SerializedProperty DuplicateElement(SerializedProperty arrayProperty, SerializedProperty toDuplicate, string name, int index, bool changeName = true)
        {
            var json = toDuplicate.CopyToJson(true);
            return PasteElement(arrayProperty, json, index, out _, name, changeName);
        }

        #endregion

        internal static List<SerializedProperty> GetBindingsForComposite(SerializedProperty bindingsArray, int indexOfComposite)
        {
            var compositeBindings = new List<SerializedProperty>();
            var compositeStartIndex = InputActionSerializationHelpers.GetCompositeStartIndex(bindingsArray, indexOfComposite);
            if (compositeStartIndex == -1)
                return compositeBindings;

            for (var i = compositeStartIndex + 1; i < bindingsArray.arraySize; ++i)
            {
                var bindingProperty = bindingsArray.GetArrayElementAtIndex(i);
                var bindingFlags = (InputBinding.Flags)bindingProperty.FindPropertyRelative("m_Flags").intValue;
                if ((bindingFlags & InputBinding.Flags.PartOfComposite) == 0)
                    break;
                compositeBindings.Add(bindingProperty);
            }
            return compositeBindings;
        }
    }
}

#endif

using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "StatAugmentDatabase", menuName = "Spiral Survivors/Stat Augment Database")]
public class StatAugmentDatabase : ScriptableObject
{
    public List<StatAugmentEntry> stats = new();
}